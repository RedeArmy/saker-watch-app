namespace SakerWatch.BuildingBlocks.Kernel.Results;

/// <summary>
/// Common shape of <see cref="Result"/> and <see cref="Result{T}"/>, so generic pipeline code can
/// build a failure of any result type without reflection.
/// </summary>
/// <typeparam name="TSelf">The concrete result type.</typeparam>
public interface IResult<TSelf>
    where TSelf : IResult<TSelf>
{
    /// <summary>True when the operation succeeded.</summary>
    bool IsSuccess { get; }

    /// <summary>The failure, or <see langword="null"/> on success.</summary>
    Error? Error { get; }

    /// <summary>Creates a failed result carrying <paramref name="error"/>.</summary>
    static abstract TSelf FromError(Error error);
}

/// <summary>Outcome of an operation without a value: success, or an expected <see cref="Kernel.Results.Error"/>.</summary>
public readonly record struct Result : IResult<Result>
{
    private Result(Error? error) => Error = error;

    /// <inheritdoc />
    public bool IsSuccess => Error is null;

    /// <inheritdoc />
    public Error? Error { get; }

    /// <summary>A successful result.</summary>
    public static Result Success() => new(null);

    /// <summary>Wraps <paramref name="value"/> in a successful <see cref="Result{T}"/>.</summary>
    public static Result<T> Success<T>(T value) => new(value, null);

    /// <summary>A failed result.</summary>
    public static Result Failure(Error error) => new(error ?? throw new ArgumentNullException(nameof(error)));

    /// <summary>Creates a failed <see cref="Result{T}"/>.</summary>
    public static Result<T> Failure<T>(Error error) => new(default, error ?? throw new ArgumentNullException(nameof(error)));

    /// <inheritdoc />
    public static Result FromError(Error error) => Failure(error);

    /// <summary>Converts an error into a failed result so handlers can <c>return error;</c>.</summary>
    public static implicit operator Result(Error error) => Failure(error);
}

/// <summary>Outcome of an operation that yields <typeparamref name="T"/> on success. Create it through <see cref="Result"/>.</summary>
/// <typeparam name="T">The success value type.</typeparam>
public readonly record struct Result<T> : IResult<Result<T>>
{
    private readonly T? _value;

    internal Result(T? value, Error? error)
    {
        _value = value;
        Error = error;
    }

    /// <inheritdoc />
    public bool IsSuccess => Error is null;

    /// <inheritdoc />
    public Error? Error { get; }

    /// <summary>The success value; reading it from a failed result is a programming error.</summary>
    /// <exception cref="InvalidOperationException">The result is a failure.</exception>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Cannot read the value of a failed result ({Error!.Code}).");

    /// <summary>Converts a value into a successful result.</summary>
    public static implicit operator Result<T>(T value) => Result.Success(value);

    /// <summary>Converts an error into a failed result so handlers can <c>return error;</c>.</summary>
    public static implicit operator Result<T>(Error error) => Result.Failure<T>(error);

    /// <inheritdoc />
    static Result<T> IResult<Result<T>>.FromError(Error error) => Result.Failure<T>(error);
}
