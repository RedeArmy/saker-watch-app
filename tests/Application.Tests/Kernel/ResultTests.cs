using AwesomeAssertions;
using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.Application.Tests.Kernel;

public sealed class ResultTests
{
    private static readonly Error _notFound = Error.NotFound("demo.missing", "Missing.");

    [Fact]
    public void Success_WithoutValue_IsSuccessWithoutError()
    {
        var result = Result.Success();

        result.IsSuccess.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    [Fact]
    public void Default_Result_IsAFailure_NotASuccess()
    {
        Result unassigned = default;

        unassigned.IsSuccess.Should().BeFalse("an operation that never reported must not read as one that succeeded");
        unassigned.Error!.Code.Should().Be(Result.UninitializedCode);
        unassigned.Error.Type.Should().Be(ErrorType.Failure);
    }

    [Fact]
    public void Default_GenericResult_IsAFailure_AndItsValueCannotBeRead()
    {
        Result<string> unassigned = default;

        unassigned.IsSuccess.Should().BeFalse();
        unassigned.Error!.Code.Should().Be(Result.UninitializedCode);
        Func<string> read = () => unassigned.Value;
        read.Should().Throw<InvalidOperationException>().WithMessage($"*{Result.UninitializedCode}*");
    }

    [Fact]
    public void Success_WithANullValue_IsStillASuccess()
    {
        Result<string?> result = Result.Success<string?>(null);

        result.IsSuccess.Should().BeTrue("a null value that was reported is different from nothing reported");
        result.Value.Should().BeNull();
    }

    [Fact]
    public void ResultsOfTheSameOutcome_AreEqual_AndADefaultDiffersFromASuccess()
    {
        Result.Success().Should().Be(Result.Success());
        Result.Success(5).Should().Be(Result.Success(5));
        default(Result).Should().NotBe(Result.Success());
    }

    [Fact]
    public void Success_WithValue_ExposesValue()
    {
        var result = Result.Success(42);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    [Fact]
    public void Failure_Generic_ExposesErrorAndRejectsValueAccess()
    {
        var result = Result.Failure<int>(_notFound);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(_notFound);
        Func<int> read = () => result.Value;
        read.Should().Throw<InvalidOperationException>().WithMessage("*demo.missing*");
    }

    [Fact]
    public void ImplicitConversions_FromValueAndError_BuildExpectedResults()
    {
        Result<string> ok = "value";
        Result<string> failed = _notFound;
        Result plain = _notFound;

        ok.Value.Should().Be("value");
        failed.Error.Should().Be(_notFound);
        plain.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Failure_WithNullError_Throws()
    {
        Action act = () => Result.Failure(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(ErrorType.Failure)]
    [InlineData(ErrorType.Validation)]
    [InlineData(ErrorType.Unauthorized)]
    [InlineData(ErrorType.Forbidden)]
    [InlineData(ErrorType.NotFound)]
    [InlineData(ErrorType.Conflict)]
    [InlineData(ErrorType.Unprocessable)]
    public void ErrorFactories_SetMatchingType(ErrorType type)
    {
        Error error = type switch
        {
            ErrorType.Failure => Error.Failure("c", "m"),
            ErrorType.Validation => Error.Validation("c", "m"),
            ErrorType.Unauthorized => Error.Unauthorized("c", "m"),
            ErrorType.Forbidden => Error.Forbidden("c", "m"),
            ErrorType.NotFound => Error.NotFound("c", "m"),
            ErrorType.Conflict => Error.Conflict("c", "m"),
            ErrorType.Unprocessable => Error.Unprocessable("c", "m"),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

        error.Type.Should().Be(type);
        error.Code.Should().Be("c");
    }
}
