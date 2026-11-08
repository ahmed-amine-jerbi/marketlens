using System.Text.Json;
using System.Text.Json.Nodes;

namespace Marketlens.Api.Tests.Http;

/// <summary>
/// The committed <c>services/api/openapi/v1.json</c> is the contract the TypeScript client types are
/// generated from (D39). This test fails whenever the API's live document differs from it, so a
/// contract change is always a reviewed file change. Set <c>MARKETLENS_UPDATE_OPENAPI=1</c> to rewrite
/// the file after an intended change, then regenerate the types with <c>npm run generate:api</c>.
/// </summary>
public sealed class OpenApiContractTests(MarketlensApiFactory factory)
{
    private const string UpdateVariable = "MARKETLENS_UPDATE_OPENAPI";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [Fact]
    public async Task Committed_openapi_document_matches_the_api()
    {
        using var client = factory.CreateClient();
        var live = Normalize(await client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken));
        var committedPath = Path.Combine(FindApiDirectory(), "openapi", "v1.json");

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(committedPath)!);
            await File.WriteAllTextAsync(committedPath, live, TestContext.Current.CancellationToken);
        }

        Assert.True(File.Exists(committedPath), $"{committedPath} is missing; run the tests with {UpdateVariable}=1.");
        var committed = Normalize(await File.ReadAllTextAsync(committedPath, TestContext.Current.CancellationToken));
        Assert.True(committed == live, $"The API contract changed. Review it, run the tests with {UpdateVariable}=1, then `npm run generate:api`.");
    }

    // Line endings and indentation must not count as a contract change.
    private static string Normalize(string json) => JsonNode.Parse(json)!.ToJsonString(Indented).ReplaceLineEndings("\n") + "\n";

    private static string FindApiDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Marketlens.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Marketlens.slnx not found above the test output directory.");
    }
}
