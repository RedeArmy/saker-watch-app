using Microsoft.AspNetCore.Http.HttpResults;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Web.Idempotency;
using SakerWatch.BuildingBlocks.Web.Problems;
using SakerWatch.Identity.Application.Features.GetMe;
using SakerWatch.Identity.Application.Features.UpdatePreferences;

namespace SakerWatch.Hosts.Api.Api;

/// <summary>The caller's own profile.</summary>
/// <param name="Id">Internal user id (UUIDv7), never the identity provider's subject.</param>
/// <param name="DisplayName">Name sent by the identity provider, if any.</param>
/// <param name="Email">Email sent by the identity provider, if any.</param>
/// <param name="Locale">Preferred UI locale (BCP 47), for example <c>es-GT</c>.</param>
/// <param name="CreatedAt">When the account was first seen (ISO-8601 UTC).</param>
public sealed record MeResponse(Guid Id, string? DisplayName, string? Email, string Locale, DateTimeOffset CreatedAt);

/// <summary>Body of <c>PUT /v1/me/preferences</c>.</summary>
/// <param name="Locale">Preferred UI locale: <c>es-GT</c> or <c>en-US</c>.</param>
public sealed record UpdatePreferencesRequest(string Locale);

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

        v1.MapPut("/me/preferences", UpdatePreferencesAsync)
            .WithName("UpdateMyPreferences")
            .WithSummary("Set the current user's preferences")
            .WithDescription("Sets the preferred UI locale and returns the updated profile. Requires an Idempotency-Key: repeating the request with the same key and body replays the first response (Idempotent-Replayed: true); the same key with a different body is rejected with 422.")
            .WithTags(_tag)
            .RequireIdempotencyKey()
            .Accepts<UpdatePreferencesRequest>("application/json")
            .Produces<MeResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
    }

    private static async Task<IResult> GetMeAsync(ISender sender, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetMeQuery(), cancellationToken);
        return result.ToHttpResult(me => TypedResults.Ok(new MeResponse(me.Id, me.DisplayName, me.Email, me.Locale, me.CreatedAt)));
    }

    private static async Task<IResult> UpdatePreferencesAsync(UpdatePreferencesRequest body, HttpContext http, ISender sender, CancellationToken cancellationToken)
    {
        var key = IdempotencyKeyReader.Require(http);
        if (!key.IsSuccess)
        {
            return key.Error!.ToProblem();
        }

        var result = await sender.Send(new UpdatePreferencesCommand(body.Locale, key.Value), cancellationToken);
        return result.ToHttpResult(me => TypedResults.Ok(new MeResponse(me.Id, me.DisplayName, me.Email, me.Locale, me.CreatedAt)));
    }
}
