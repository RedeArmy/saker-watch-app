using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.BuildingBlocks.Application.Idempotency;

/// <summary>
/// Serializes a successful <see cref="Result"/> or <see cref="Result{T}"/> so a repeated request can be answered with it,
/// and restores it. Only successes are stored: failures roll the unit of work back and leave no key (ADR-0022).
/// </summary>
public static class ResultSnapshot
{
    /// <summary>Largest body stored; bigger responses are not snapshotted and a replay points to a GET.</summary>
    public const int MaxBodyBytes = 64 * 1024;

    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    /// <summary>The body to store for a successful <paramref name="response"/>, or <see langword="null"/> when it exceeds <see cref="MaxBodyBytes"/>.</summary>
    public static byte[]? Serialize<TResponse>(TResponse response)
        where TResponse : IResult<TResponse>
    {
        if (!response.IsSuccess)
        {
            throw new InvalidOperationException("Only successful results are snapshotted.");
        }

        if (SnapshotShape<TResponse>.ValueType is not { } valueType)
        {
            return "{}"u8.ToArray();
        }

        byte[] body = JsonSerializer.SerializeToUtf8Bytes(SnapshotShape<TResponse>.ValueProperty!.GetValue(response), valueType, _options);
        return body.Length <= MaxBodyBytes ? body : null;
    }

    /// <summary>Rebuilds the stored successful result.</summary>
    public static TResponse Restore<TResponse>(byte[] body)
        where TResponse : IResult<TResponse>
    {
        if (SnapshotShape<TResponse>.ValueType is not { } valueType)
        {
            return (TResponse)(object)Result.Success();
        }

        object? value = JsonSerializer.Deserialize(body, valueType, _options);
        return (TResponse)SnapshotShape<TResponse>.Success!.Invoke(null, [value])!;
    }

    [SuppressMessage("Major Bug", "S2743:Static fields should not be used in generic types", Justification = "Intentional per-result-type cache of the value type and its accessors.")]
    private static class SnapshotShape<TResponse>
    {
        public static readonly Type? ValueType = typeof(TResponse).IsGenericType ? typeof(TResponse).GetGenericArguments()[0] : null;

        public static readonly PropertyInfo? ValueProperty = ValueType is null ? null : typeof(TResponse).GetProperty("Value");

        public static readonly MethodInfo? Success = ValueType is null
            ? null
            : typeof(Result).GetMethods()
                .Single(m => m is { Name: nameof(Result.Success), IsGenericMethodDefinition: true } && m.GetParameters().Length == 1)
                .MakeGenericMethod(ValueType);
    }
}
