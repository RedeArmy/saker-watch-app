using FluentValidation;
using FluentValidation.Results;
using SakerWatch.BuildingBlocks.Application.Messaging;
using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.BuildingBlocks.Application.Behaviors;

/// <summary>
/// Runs every FluentValidation validator registered for the request and short-circuits with a
/// <see cref="ErrorType.Validation"/> error, so handlers only ever see valid input.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The result type.</typeparam>
/// <param name="validators">Validators registered for <typeparamref name="TRequest"/> (possibly none).</param>
public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : IResult<TResponse>
{
    /// <summary>Stable code of the validation error.</summary>
    public const string ErrorCode = "validation.failed";

    private readonly IValidator<TRequest>[] _validators = [.. validators];

    /// <inheritdoc />
    public async ValueTask<TResponse> Handle(TRequest request, PipelineNext<TResponse> next, CancellationToken cancellationToken)
    {
        if (_validators.Length == 0)
        {
            return await next();
        }

        var context = new ValidationContext<TRequest>(request);
        var failures = new List<ValidationFailure>();
        foreach (IValidator<TRequest> validator in _validators)
        {
            ValidationResult result = await validator.ValidateAsync(context, cancellationToken);
            failures.AddRange(result.Errors);
        }

        if (failures.Count == 0)
        {
            return await next();
        }

        var details = failures
            .GroupBy(f => FieldName(f.PropertyName), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(f => f.ErrorMessage).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

        return TResponse.FromError(Error.Validation(ErrorCode, "One or more validation errors occurred.", details));
    }

    /// <summary>
    /// The name a client knows a field by: the API speaks camelCase JSON, but validators report C# property paths. Every segment of a
    /// path such as <c>Identity.DisplayName</c> or <c>Items[0].Ticker</c> is converted (<c>identity.displayName</c>, <c>items[0].ticker</c>).
    /// </summary>
    internal static string FieldName(string propertyPath) =>
        string.Join('.', propertyPath.Split('.').Select(segment => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(segment)));
}
