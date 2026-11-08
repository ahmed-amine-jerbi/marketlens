using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;

using Marketlens.Api.MarketData;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace Marketlens.Api.Http;

public sealed class RateLimitOptions
{
    public const string Section = "RateLimits";

    /// <summary>
    /// Requests per minute per client IP on the public read endpoints. One page view makes about a
    /// dozen calls and households or offices share an IP, so this sits well above one person's use.
    /// </summary>
    public int PublicPerMinute { get; set; } = 600;
}

/// <summary>
/// How the API speaks HTTP, in one place: JSON shapes (decimals as strings, D39; enums as lower-case
/// words; asset keys as text), problem details, OpenAPI, per-IP rate limits and the forwarded-header
/// trust boundary (D35).
/// </summary>
public static class ApiConventions
{
    public const string PublicRateLimit = "public";

    public static void ConfigureJson(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Converters.Add(new DecimalAsStringConverter());
        options.Converters.Add(new AssetKeyJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
    }

    public static IServiceCollection AddApiConventions(this IServiceCollection services, IConfiguration configuration)
    {
        services.ConfigureHttpJsonOptions(json => ConfigureJson(json.SerializerOptions));
        services.AddProblemDetails();
        services.AddOpenApi(openApi =>
        {
            openApi.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "Marketlens API",
                    Version = "v1",
                    Description = "Live currency, crypto, stock and ETF prices, conversions between any two assets, and chart history. Prices are decimal strings; errors are RFC 9457 problem details with urn:marketlens:problem:* types.",
                    License = new OpenApiLicense { Name = "MIT", Identifier = "MIT" },
                };

                // Relative to wherever the API is served; clients use their own base URL.
                document.Servers = [new OpenApiServer { Url = "/" }];
                document.Tags = new HashSet<OpenApiTag>
                {
                    new() { Name = "Assets", Description = "The currencies, coins, stocks and ETFs Marketlens tracks." },
                    new() { Name = "Quotes", Description = "Latest prices in any display currency." },
                    new() { Name = "Conversions", Description = "Amounts converted between any two assets." },
                    new() { Name = "History", Description = "Chart history, converted point by point." },
                    new() { Name = "Status", Description = "Freshness of each data source." },
                    new() { Name = "Locale", Description = "Language and currency suggestions for new visitors." },
                };
                return Task.CompletedTask;
            });
            openApi.AddSchemaTransformer((schema, context, _) =>
            {
                DescribeAsText(schema, context.JsonTypeInfo.Type);

                // Descriptions come from XML doc comments, whose line breaks follow the build OS; the
                // committed contract must be the same on every machine.
                schema.Description = schema.Description?.ReplaceLineEndings("\n");
                return Task.CompletedTask;
            });
            openApi.AddOperationTransformer((operation, _, _) =>
            {
                operation.Summary = operation.Summary?.ReplaceLineEndings("\n");
                operation.Description = operation.Description?.ReplaceLineEndings("\n");
                foreach (var parameter in operation.Parameters ?? [])
                {
                    parameter.Description = parameter.Description?.ReplaceLineEndings("\n");
                }

                return Task.CompletedTask;
            });
        });

        services.Configure<RateLimitOptions>(configuration.GetSection(RateLimitOptions.Section));
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (rejection, cancellationToken) => WriteRateLimitedAsync(rejection.HttpContext, rejection.Lease, cancellationToken);
            limiter.AddPolicy(PublicRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = http.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value.PublicPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });

        // Only the reverse proxy may say who the client is (D35): the configured Compose network, or
        // loopback (a local dev proxy) when none is configured. A forwarded header from anywhere else
        // is ignored, so callers can't spoof their IP past the rate limits. Both lists must never end
        // up empty: ASP.NET Core then trusts every sender.
        services.Configure<ForwardedHeadersOptions>(forwarded =>
        {
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            forwarded.ForwardLimit = 1;
            forwarded.KnownProxies.Clear();
            forwarded.KnownIPNetworks.Clear();
            var trustedNetworks = configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [];
            foreach (var network in trustedNetworks)
            {
                forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }

            if (trustedNetworks.Length == 0)
            {
                forwarded.KnownProxies.Add(IPAddress.Loopback);
                forwarded.KnownProxies.Add(IPAddress.IPv6Loopback);
            }
        });

        return services;
    }

    public static WebApplication UseApiConventions(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseForwardedHeaders();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseRateLimiter();
        app.MapOpenApi();
        return app;
    }

    private static void DescribeAsText(OpenApiSchema schema, Type type)
    {
        if (type == typeof(decimal) || type == typeof(decimal?))
        {
            schema.Type = type == typeof(decimal) ? JsonSchemaType.String : JsonSchemaType.String | JsonSchemaType.Null;
            schema.Format = "decimal";
            schema.Pattern = "^-?[0-9]+(\\.[0-9]+)?$";
            return;
        }

        if (type == typeof(AssetKey) || type == typeof(AssetKey?))
        {
            schema.Type = type == typeof(AssetKey) ? JsonSchemaType.String : JsonSchemaType.String | JsonSchemaType.Null;
            schema.Pattern = "^[a-z]+:[A-Z0-9][A-Z0-9.\\-]{0,14}$";
            schema.Properties?.Clear();
            schema.Required?.Clear();
        }
    }

    private static async ValueTask WriteRateLimitedAsync(HttpContext http, System.Threading.RateLimiting.RateLimitLease lease, CancellationToken cancellationToken)
    {
        if (lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        await ApiProblems.TooManyRequests().ExecuteAsync(http);
    }
}

/// <summary>Stable problem types (RFC 9457) the API returns; each is described in docs/api/problems.md.</summary>
public static class ApiProblems
{
    private const string TypePrefix = "urn:marketlens:problem:";

    public static ProblemHttpResult InvalidParameter(string parameter, string detail) =>
        Problem(HttpStatusCode.BadRequest, "invalid-parameter", "A request parameter is invalid.", detail, ("parameter", parameter));

    public static ProblemHttpResult UnknownAsset(AssetKey asset) =>
        Problem(HttpStatusCode.NotFound, "unknown-asset", "Marketlens doesn't track this asset.", $"No asset '{asset}'.", ("asset", asset.ToString()));

    public static ProblemHttpResult RateUnavailable(AssetKey asset) =>
        Problem(HttpStatusCode.ServiceUnavailable, "rate-unavailable", "A price needed for this answer isn't available yet.", $"There is no price for '{asset}' yet; its provider may still be loading or unreachable.", ("asset", asset.ToString()));

    public static ProblemHttpResult TooManyRequests() =>
        Problem(HttpStatusCode.TooManyRequests, "too-many-requests", "Too many requests.", "Slow down and retry after the time in the Retry-After header.");

    private static ProblemHttpResult Problem(HttpStatusCode status, string type, string title, string detail, params (string Key, object? Value)[] extensions) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: (int)status,
            title: title,
            type: TypePrefix + type,
            extensions: extensions.ToDictionary(extension => extension.Key, extension => extension.Value));
}

/// <summary>Writes decimals as JSON strings without trailing zeros, so no client ever parses money into a binary float (D39).</summary>
public sealed class DecimalAsStringConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String when decimal.TryParse(reader.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) => value,
        JsonTokenType.Number => reader.GetDecimal(),
        _ => throw new JsonException("Expected a decimal number written as a string, such as \"0.25\"."),
    };

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString("0.############################", CultureInfo.InvariantCulture));
    }
}

public sealed class AssetKeyJsonConverter : JsonConverter<AssetKey>
{
    public override AssetKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        AssetKey.TryParse(reader.GetString(), out var key) ? key : throw new JsonException("Expected an asset key such as \"crypto:BTC\".");

    public override void Write(Utf8JsonWriter writer, AssetKey value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }
}
