# ADR-0036: The partial-purchase calculator

- **Status:** accepted (owner confirmed 2026-10-19)
- **Date:** 2026-10-19
- **Deciders:** Eder

## Context
`docs/product-spec.md` §2.2 asks for a calculator that says how many shares of a stock an amount buys and what that is in quetzales, with the exchange rate's date and source. ADR-0006 fixes the rounding (banker's for money, six decimals towards zero for shares) and lists the property tests; ADR-0018 and ADR-0028 fix the exchange path. Left open: the request shapes, where the arithmetic lives, how bounds and rounding are applied, and how another module's exchange rate is reached without breaking module boundaries.

## Options considered
- **One mode (amount to shares) or two.** The owner chose both: "invest X" and "buy N shares". A single formula pair serves both and the second costs almost nothing.
- **Round inside the arithmetic or at the edge.** Rounding inside makes the two modes disagree by a few millionths and breaks the property tests; ADR-0006 already says to round at the presentation boundary.
- **Property tests with FsCheck or parameterized tests.** The owner chose FsCheck. The tests of 2.4 to 2.6 loop over random inputs by hand; FsCheck generates across scales, shrinks failures and reports the seed.
- **Reach the exchange rate through `MarketData.Application` or a contract.** Modules talk only through `Contracts`; a contract is added (2.7b).

## Decision
- **Where it lives.** `Calculator.Domain` holds the arithmetic with no I/O (`PartialPurchaseCalculator`), written on the money kit of ADR-0037: the price and the amount are `Money`, the rate is an `ExchangeRate` and the quantity is `Shares`, so the three cannot be swapped or mixed across currencies. The use case and the endpoint come in 2.7e.
- **Two modes, one result.** `ByAmount(price, amount, rate)` gives `shares = amount / price`; `ByShares(price, shares, rate)` gives `amount = shares x price`. Both return `PartialPurchase`: shares, the money in the stock's currency, and the price and the money converted with the rate. Everything is exact.
- **Rounding only on display.** `PartialPurchase.ForDisplay()` returns a `PartialPurchaseDisplay` whose money is `DisplayMoney` (minor units of its own currency, half to even) and whose shares are `DisplayShares` (cut at six decimals towards zero, so the shown shares never cost more than the amount). These types carry no arithmetic. The price and the rate are echoed as given.
- **Bounds.** The calculator owns only the price of a share (0.000001 to 1,000,000,000: prices are stored with six decimals). The ranges of an amount of money, of a quantity of shares and of a rate are the kit's, so every figure that does not fit is a validation outcome (`money.amount_out_of_range`, `money.shares_invalid`, `money.rate_invalid`), never an exception. Shares a user asks for have at most six decimals (`Shares.FromRequest`): more would be cut on display and the purchase would not be the one asked for.
- **Property tests with FsCheck** (`FsCheck.Xunit` 3.3.4, no other new dependency), written to ADR-0006: shares shown never cost more than the amount and leave less than a millionth of a share unspent; exact shares times price is the amount; the two modes agree; more money never buys fewer shares and a dearer share never buys more; the rate converts back; doubling the amount doubles shares and quetzales; display is within half a minor unit and never rounds shares up; currencies that do not match, and inputs that are not positive, are always refused with their code. Hand-computed cases (the spec's USD 50 on a USD 600 share is 0.083333 shares) sit beside them, and each property was checked to fail when the rule it protects is broken.
- **The endpoint (2.7e).** `POST /v1/calculations/partial-purchase`, as in the spec, with no `Idempotency-Key` (a pure computation that stores nothing). The input is a stock of the catalog (`market` and `ticker`; the price is its newest close as traded, never registered from here) or a manual `price` with its `currency`, and exactly one of `amount` or `shares`. The exchange rate to quetzales comes from a new `MarketData.Contracts` port, `IFxRates`, over the converter of ADR-0028, so the path, day and source of every step travel with the answer and a flag says when the rate is stale. A stale stock price is used and reported; a stock with no price yet is `422` and a currency with no stored rate is `404`: nothing is guessed. The optional spread or fee assumption of the spec is not part of this milestone: how a fee is applied to an amount is ambiguous and is decided when asked for.

## Consequences
- The arithmetic is small and fully covered by property tests, so the application part only wires inputs.
- Shares and money may differ from a broker's: its rate, fees and minimum order sizes are its own. The answer says it is a reference.
- `FsCheck.Xunit` is now in the central package list and available to every test project.
