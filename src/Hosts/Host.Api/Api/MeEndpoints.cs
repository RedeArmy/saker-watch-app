using Microsoft.AspNetCore.Http.HttpResults;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Web.Problems;
using SakerWatch.Identity.Application.Features.GetMe;

namespace SakerWatch.Hosts.Api.Api;

/// <summary>The caller's own profile.</summary>
/// <param name="Id">Internal user id (UUIDv7), never the identity provider's subject.</param>
/// <param name="DisplayName">Name sent by the identity provider, if any.</param>
/// <param name="Email">Email sent by the identity provider, if any.</param>
/// <param name="Locale">Preferred UI locale (BCP 47), for example <c>es-GT</c>.</param>
/// <param name="CreatedAt">When the account was first seen (ISO-8601 UTC).</param>
public sealed record MeResponse(Guid Id, string? DisplayName, string? Email, string Locale, DateTimeOffset CreatedAt);

/// <summary>Endpoints under <c>/v1/me</c>.</summary>
internal static class MeEndpoints
{
    private const string _tag = "Me";

    /// <summary>Maps the endpoints on the version group.</summary>
    public static void MapMe(this RouteGroupBuilder v1)
    {
        v1.MapGet("/me", GetMeAsync)
            .WithName("GetMe")
            .WithSummary("Get the current user")
            .WithDescription("Returns the profile of the authenticated caller. The account is created on the first authenticated request.")
            .WithTags(_tag)
            .Produces<MeResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
    }

    private static async Task<IResult> GetMeAsync(ISender sender, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetMeQuery(), cancellationToken);
        return result.ToHttpResult(me => TypedResults.Ok(new MeResponse(me.Id, me.DisplayName, me.Email, me.Locale, me.CreatedAt)));
    }
}
