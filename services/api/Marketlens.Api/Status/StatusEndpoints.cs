using Marketlens.Api.MarketData;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Marketlens.Api.Status;

public sealed record SourceStatus(string Source, int QuoteCount, DateTime NewestAsOfUtc, DateTime OldestAsOfUtc, Freshness Freshness);

public sealed record StatusResponse(DateTime CheckedAtUtc, string StockData, IReadOnlyList<SourceStatus> Sources);

/// <summary>
/// <c>GET /api/v1/status</c>: how current each data source is, so a stale or silent provider is
/// visible to clients and operators (R13).
/// </summary>
public static class StatusEndpoints
{
    public static RouteGroupBuilder MapStatusEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/status", GetStatus).WithName("GetStatus").WithTags("Status")
            .WithSummary("Show how current each data source is");
        return api;
    }

    private static Ok<StatusResponse> GetStatus(QuoteBook quoteBook, IOptions<FinnhubOptions> finnhub, TimeProvider timeProvider)
    {
        var sources = quoteBook.Snapshot()
            .GroupBy(quote => quote.Source)
            .Select(source => new SourceStatus(
                source.Key,
                source.Count(),
                source.Max(quote => quote.AsOfUtc),
                source.Min(quote => quote.AsOfUtc),
                source.Max(quote => quote.Freshness)))
            .OrderBy(source => source.Source, StringComparer.Ordinal)
            .ToList();

        var stockData = finnhub.Value.HasApiKey ? FinnhubClient.SourceName : SampleStockFeed.SourceName;
        return TypedResults.Ok(new StatusResponse(timeProvider.GetUtcNow().UtcDateTime, stockData, sources));
    }
}
