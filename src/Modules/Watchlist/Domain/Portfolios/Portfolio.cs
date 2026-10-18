using SakerWatch.BuildingBlocks.Kernel.Domain;
using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.Watchlist.Domain.Portfolios;

/// <summary>Identifier of a <see cref="Portfolio"/>: a UUIDv7 from the injected clock.</summary>
/// <param name="Value">The underlying UUID.</param>
public readonly record struct PortfolioId(Guid Value) : IStronglyTypedId<PortfolioId>
{
    /// <inheritdoc />
    public static PortfolioId Create(Guid value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// The name of a portfolio. Spaces at both ends are removed here, so no path can store an untrimmed name; a name that is empty after that, longer
/// than <see cref="MaxLength"/> or holding a control character is refused. Two names are the same when they differ only in letter case
/// (<see cref="Key"/>), which is what the database index compares (ADR-0026).
/// </summary>
public sealed class PortfolioName : ValueObject
{
    /// <summary>Longest accepted name.</summary>
    public const int MaxLength = 60;

    private PortfolioName(string value) => Value = value;

    /// <summary>The trimmed name, as the user wrote it.</summary>
    public string Value { get; }

    /// <summary>The name in lower case, the key of the per-user uniqueness.</summary>
    public string Key => Value.ToLowerInvariant();

    /// <summary>Trims and validates <paramref name="value"/>.</summary>
    public static Result<PortfolioName> Create(string? value)
    {
        string trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return Error.Validation("watchlist.portfolio.name_required", "A portfolio needs a name.");
        }

        if (trimmed.Length > MaxLength)
        {
            return Error.Validation("watchlist.portfolio.name_too_long", $"A portfolio name has at most {MaxLength} characters.");
        }

        return trimmed.Any(char.IsControl)
            ? Error.Validation("watchlist.portfolio.name_invalid", "A portfolio name has no control characters.")
            : new PortfolioName(trimmed);
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Key;
    }
}

/// <summary>
/// A named group of a user's watchlist stocks (ADR-0026). A stock may be in several portfolios or in none; the membership lives beside the
/// portfolio so a portfolio never loads its stocks to be renamed or moved.
/// </summary>
public sealed class Portfolio : AggregateRoot<PortfolioId>
{
    /// <summary>Longest accepted description.</summary>
    public const int MaxDescriptionLength = 500;

    private Portfolio(PortfolioId id, Guid userId, PortfolioName name, string? description, int position, DateTimeOffset createdAt)
        : base(id)
    {
        UserId = userId;
        Name = name;
        Description = description;
        Position = position;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    /// <summary>The owner.</summary>
    public Guid UserId { get; }

    /// <summary>The name, unique per user ignoring letter case.</summary>
    public PortfolioName Name { get; private set; }

    /// <summary>A free description, or <see langword="null"/>.</summary>
    public string? Description { get; private set; }

    /// <summary>Where the portfolio sits among the user's portfolios, from zero.</summary>
    public int Position { get; private set; }

    /// <summary>When the portfolio was created.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>When the portfolio last changed.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Creates a portfolio at the given position.</summary>
    public static Result<Portfolio> Create(PortfolioId id, Guid userId, string? name, string? description, int position, DateTimeOffset now)
    {
        if (userId == Guid.Empty)
        {
            return Error.Validation("watchlist.portfolio.user_required", "A portfolio needs an owner.");
        }

        if (position < 0)
        {
            return Error.Validation("watchlist.portfolio.position_invalid", "A position is zero or more.");
        }

        Result<PortfolioName> validName = PortfolioName.Create(name);
        if (!validName.IsSuccess)
        {
            return validName.Error!;
        }

        Result<string?> validDescription = NormalizeDescription(description);
        return validDescription.IsSuccess
            ? new Portfolio(id, userId, validName.Value, validDescription.Value, position, now)
            : validDescription.Error!;
    }

    /// <summary>Rebuilds a portfolio from storage; the values were validated when written and the database repeats the checks.</summary>
    public static Portfolio Restore(PortfolioId id, Guid userId, PortfolioName name, string? description, int position, DateTimeOffset createdAt, DateTimeOffset updatedAt) =>
        new(id, userId, name, description, position, createdAt) { UpdatedAt = updatedAt };

    /// <summary>Normalizes a description: trimmed, and <see langword="null"/> when nothing is left.</summary>
    public static Result<string?> NormalizeDescription(string? description)
    {
        string? trimmed = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        return trimmed is { Length: > MaxDescriptionLength }
            ? Error.Validation("watchlist.portfolio.description_too_long", $"A description has at most {MaxDescriptionLength} characters.")
            : Result.Success(trimmed);
    }

    /// <summary>Changes the name and the description. Both are validated first and nothing changes when either is refused.</summary>
    public Result Edit(string? name, string? description, DateTimeOffset now)
    {
        Result<PortfolioName> validName = PortfolioName.Create(name);
        if (!validName.IsSuccess)
        {
            return validName.Error!;
        }

        Result<string?> validDescription = NormalizeDescription(description);
        if (!validDescription.IsSuccess)
        {
            return validDescription.Error!;
        }

        if (validName.Value.Value == Name.Value && validDescription.Value == Description)
        {
            return Result.Success();
        }

        Name = validName.Value;
        Description = validDescription.Value;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Moves the portfolio to another position.</summary>
    public Result MoveTo(int position, DateTimeOffset now)
    {
        if (position < 0)
        {
            return Error.Validation("watchlist.portfolio.position_invalid", "A position is zero or more.");
        }

        if (position != Position)
        {
            Position = position;
            UpdatedAt = now;
        }

        return Result.Success();
    }
}
