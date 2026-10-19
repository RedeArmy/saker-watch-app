# ADR-0037: A money kit: exact amounts, typed currencies and rates, rounding only on display

- **Status:** proposed
- **Date:** 2026-10-19
- **Deciders:** Eder

## Context
ADR-0006 says money is `decimal`, calculated at full precision and rounded only at the presentation boundary. Until now that was a convention: prices, amounts, rates and quantities were all bare `decimal`, so nothing stopped a rate from being applied the wrong way round, two currencies from being added, a rounded figure from being reused in a calculation, or an overflow from throwing in the middle of a request. The calculator (2.7) makes the risk concrete: a price, an amount and a rate go into one function as three interchangeable numbers. The owner asked for the handling a banking system would have, so that no digit is lost and no error escapes.

## Options considered
- **Convention and review only.** Cheapest, and the status quo; it does not stop a mistake.
- **A policy ADR plus an architecture rule that forbids decimal arithmetic outside certain files.** Catches little and is easy to work around.
- **Typed values in the Kernel that make the wrong thing impossible to write.** Chosen; the owner picked the full version, including moving `CurrencyCode` and adopting the kit in the existing FX and performance code.
- **Integer minor units (a ledger style).** Banks store cents because they only ever add and subtract whole cents. This system multiplies by rates and divides by prices, and ADR-0006 keeps full precision until display, so exact `decimal` is the right base, with rounding as a separate, explicit step.

## Decision
`SakerWatch.BuildingBlocks.Kernel.Monetary` holds the types; none has a public constructor, each is built by a factory that returns a `Result`, and all are immutable and compare by value.

- **`CurrencyCode`** (moved from `MarketData.Domain`): three letters, and its `MinorUnits` (0, 2 or 3).
- **`Money`**: an exact `decimal` and its currency. `Add` and `Subtract` need the same currency; `Times` and `DividedBy` take a plain number; every answer is a `Result`. An amount, or the result of an operation, beyond ±10^15 is a validation outcome. Because every operand is bounded and every operation is guarded, nothing overflows and nothing throws: a property test feeds arbitrary and extreme decimals through every operation to prove it.
- **`ExchangeRate`**: a direction (`From` to `To`) and a positive rate between 10^-12 and 10^12. `Convert` accepts only money in `From`, so a USD to GTQ rate is never applied to quetzales. `Invert` and `Then` (chaining) are exact; chaining a rate back to the currency it started from is refused, because a currency relates to itself only through the identity. A rate between a currency and itself exists only as `Identity`.
- **`Shares`**: an exact positive quantity up to 10^12. A user's request may have at most six decimals and is refused otherwise instead of being cut. `Buying(amount, price)` and `Cost(price)` check currency and price.
- **`RelativeChange`**: `latest / baseline - 1` as an exact fraction, from a baseline that must be positive.
- **Rounding happens in one place per type, and its result cannot be calculated with.** `Money.ToDisplay()` rounds to the minor units of the currency, half to even; `Shares.ToDisplay()` cuts at six decimals towards zero (so a shown purchase never states more than the money pays for). They return `DisplayMoney` and `DisplayShares`, which have no arithmetic. An architecture test enforces both that the types are only built through their factories and that the display types carry no operators.
- **Text does not depend on the culture:** `ToString` and the messages of the errors format numbers with the invariant culture.
- **No `double`, no `float`**, as before. On the wire decimals are strings (the converter of 2.6), and in the database `numeric`.
- **Adoption.** Part 2.7b adds the kit and moves `CurrencyCode`. Part 2.7c makes the FX code (`FxRate`, `FxConversion`, `FxConverter`) and the performance windows use it. Part 2.7d rebuilds the calculator on it, and 2.7e adds the endpoint.

## Consequences
- A whole class of mistakes (mixed currencies, a reversed rate, double rounding, an overflow in a handler) stops compiling or becomes a `Result`.
- `decimal` keeps 28 or 29 significant digits: a quotient such as one third is not exact. The kit keeps every digit it has, rounds nothing before display, and its tests allow a tolerance of 10^-20 where an identity involves a division.
- The bounds are generous (a thousand million million) but real; an amount beyond them is refused rather than computed.
- Existing code that still uses bare `decimal` is converted at the boundary (2.7c); until then the two coexist.
