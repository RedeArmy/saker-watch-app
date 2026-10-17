using SakerWatch.BuildingBlocks.Kernel.Domain;

namespace SakerWatch.MarketData.Domain.Stocks;

/// <summary>Identifier of a <see cref="Stock"/>: a UUIDv7 from the injected clock (<c>IIdGenerator</c>).</summary>
/// <param name="Value">The underlying UUID.</param>
public readonly record struct StockId(Guid Value) : IStronglyTypedId<StockId>
{
    /// <inheritdoc />
    public static StockId Create(Guid value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}
