using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;

namespace SakerWatch.Contract.Tests;

/// <summary>
/// The generated OpenAPI document is the contract (ADR-0014): it is committed at <c>docs/api/openapi.v1.json</c> and any change must
/// be reviewed. Run with <c>UPDATE_OPENAPI=1</c> to rewrite the file after an intentional change.
/// </summary>
public sealed class OpenApiContractTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions _pretty = new() { WriteIndented = true };

    internal static string ContractPath()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "SakerWatch.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return Path.Combine(directory ?? throw new InvalidOperationException("Repository root not found."), "docs", "api", "openapi.v1.json");
    }

    private async Task<string> GenerateAsync()
    {
        using HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        JsonNode document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return document.ToJsonString(_pretty).ReplaceLineEndings("\n") + "\n";
    }

    [Fact]
    public async Task GeneratedDocument_MatchesTheCommittedContract()
    {
        string generated = await GenerateAsync();
        string path = ContractPath();

        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1")
        {
            await File.WriteAllTextAsync(path, generated);
            return;
        }

        File.Exists(path).Should().BeTrue("docs/api/openapi.v1.json is the committed contract; generate it with UPDATE_OPENAPI=1");
        string committed = (await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n");
        generated.Should().Be(committed, "the API surface changed: review the diff and regenerate with UPDATE_OPENAPI=1 (ADR-0014)");
    }

    [Fact]
    public async Task Document_DeclaresVersionOneBearerSecurityAndNoServers()
    {
        JsonNode document = JsonNode.Parse(await GenerateAsync())!;

        document["openapi"]!.GetValue<string>().Should().StartWith("3.");
        document["info"]!["version"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        document["components"]!["securitySchemes"]!["bearer"]!["scheme"]!.GetValue<string>().Should().Be("bearer");
        document["paths"]!.AsObject().Select(path => path.Key).Should().OnlyContain(path => path.StartsWith("/v1/", StringComparison.Ordinal));
    }
}
