using Asp.Versioning;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SakerWatch.BuildingBlocks.Web.Idempotency;

namespace SakerWatch.Contract.Tests;

/// <summary>Every mapped endpoint is documented, versioned and protected as the API guidelines require (docs/api/README.md).</summary>
public sealed class EndpointConventionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly string[] _infrastructurePrefixes = ["/health", "/openapi", "/swagger"];

    private RouteEndpoint[] ApiEndpoints()
    {
        _ = factory.Server;
        return factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => !_infrastructurePrefixes.Any(prefix => endpoint.RoutePattern.RawText!.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();
    }

    [Fact]
    public void ThereIsAtLeastOneApiEndpoint_SoTheRulesAreNotVacuous()
    {
        ApiEndpoints().Should().NotBeEmpty();
    }

    [Fact]
    public void EveryEndpoint_IsUnderTheVersionSegment_AndDeclaresItsVersion()
    {
        foreach (RouteEndpoint endpoint in ApiEndpoints())
        {
            endpoint.RoutePattern.RawText.Should().StartWith("/v{version:apiVersion}/", $"{endpoint.DisplayName} must be versioned");
            endpoint.Metadata.GetMetadata<ApiVersionMetadata>().Should().NotBeNull($"{endpoint.DisplayName} must declare an API version");
        }
    }

    [Fact]
    public void EveryEndpoint_HasNameSummaryDescriptionAndTags()
    {
        foreach (RouteEndpoint endpoint in ApiEndpoints())
        {
            string who = endpoint.DisplayName ?? endpoint.RoutePattern.RawText!;
            endpoint.Metadata.GetMetadata<IEndpointNameMetadata>().Should().NotBeNull($"{who} needs WithName");
            endpoint.Metadata.GetMetadata<IEndpointSummaryMetadata>().Should().NotBeNull($"{who} needs WithSummary");
            endpoint.Metadata.GetMetadata<IEndpointDescriptionMetadata>().Should().NotBeNull($"{who} needs WithDescription");
            endpoint.Metadata.GetMetadata<ITagsMetadata>().Should().NotBeNull($"{who} needs WithTags");
        }
    }

    [Fact]
    public void EveryEndpoint_DeclaresASuccessResponse_AndTheStandardProblemResponses()
    {
        foreach (RouteEndpoint endpoint in ApiEndpoints())
        {
            string who = endpoint.DisplayName ?? endpoint.RoutePattern.RawText!;
            int[] statuses = endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>().Select(m => m.StatusCode).ToArray();

            statuses.Any(status => status is >= 200 and < 300).Should().BeTrue($"{who} must document its success response");
            statuses.Should().Contain(StatusCodes.Status401Unauthorized, $"{who} must document 401");
            statuses.Should().Contain(StatusCodes.Status500InternalServerError, $"{who} must document 500");
            statuses.Should().Contain(StatusCodes.Status503ServiceUnavailable, $"{who} must document 503: the database refusing to wait answers it from any endpoint");
        }
    }

    [Fact]
    public void NoApiEndpoint_IsAnonymous_UnlessItIsOnTheAllowList()
    {
        foreach (RouteEndpoint endpoint in ApiEndpoints())
        {
            endpoint.Metadata.GetMetadata<IAllowAnonymous>().Should().BeNull($"{endpoint.DisplayName} must not be open to anonymous callers (default-deny)");
        }
    }

    [Fact]
    public void EveryMutatingEndpoint_RequiresAnIdempotencyKey_OrIsExplicitlyExempt()
    {
        string[] safe = [HttpMethods.Get, HttpMethods.Head, HttpMethods.Options];
        foreach (RouteEndpoint endpoint in ApiEndpoints())
        {
            string[] methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.ToArray();
            if (methods.All(method => safe.Contains(method, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            bool declared = endpoint.Metadata.GetMetadata<RequiresIdempotencyKeyMetadata>() is not null
                || endpoint.Metadata.GetMetadata<IdempotencyExemptMetadata>() is not null;
            declared.Should().BeTrue($"{endpoint.DisplayName} mutates state and must call RequireIdempotencyKey() or ExemptFromIdempotencyKey(reason)");
        }
    }

    [Fact]
    public void EveryMutatingEndpoint_DocumentsTheIdempotencyResponses()
    {
        foreach (RouteEndpoint endpoint in ApiEndpoints().Where(e => e.Metadata.GetMetadata<RequiresIdempotencyKeyMetadata>() is not null))
        {
            int[] statuses = endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>().Select(m => m.StatusCode).ToArray();

            statuses.Should().Contain([StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict, StatusCodes.Status422UnprocessableEntity], $"{endpoint.DisplayName} must document 400, 409 and 422");
        }
    }
}
