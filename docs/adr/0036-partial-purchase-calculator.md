# ADR-0036: The partial-purchase calculator

- **Status:** proposed
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
- **Where it lives.** `Calculator.Domain` holds the arithmetic with no I/O (`PartialPurchaseCalculator`), so a client can reimplement the same two formulas. The use case and the endpoint come in 2.7b.
- **Two modes, one result.** `ByAmount(price, amount, rate)` gives `shares = amount / price`; `ByShares(price, shares, rate)` gives `amount = shares × price`. Both return `PartialPurchase`: shares, the money in the stock's currency, the cost of one share in quetzales and the money in quetzales (`× rate`). Everything is `decimal` and exact.
- **Rounding only on display.** `PartialPurchase.ForDisplay()` rounds money to the minor units of its currency (0 for JPY and a few others, 2 by default, 3 for the dinars) with `ToEven`, quetzales to two, and cuts shares at six decimals towards zero, so the shown shares never cost more than the amount. The price echoed back is not rounded.
- **Bounds that keep the arithmetic honest.** Price 0.000001 to 1,000,000,000 (prices are stored with six decimals); amount and shares up to 10^12; rate 0.00000001 to 10^6 (rates are stored with eight). Inside them no product or quotient can overflow a `decimal`. Shares given by the user must have at most six decimals: more would be cut on display and the purchase would not be the one asked for, so it is refused rather than changed. A bad input is a validation `Result`, never an exception.
- **Property tests with FsCheck** (`FsCheck.Xunit` 3.3.4, no other new dependency), written to ADR-0006: shares shown never cost more than the amount and leave less than a millionth of a share unspent; exact shares times price is the amount; the two modes agree; more money never buys fewer shares and a dearer share never buys more; the rate converts back; doubling the amount doubles shares and quetzales; display is within half a minor unit and never rounds shares up; non-positive inputs are always refused with their code. Hand-computed cases (the spec's USD 50 on a USD 600 share is 0.083333 shares) sit beside them, and each property was checked to fail when the rule it protects is broken.
- **For the next part (2.7b).** `POST /v1/calculations/partial-purchase` as in the spec, no `Idempotency-Key` (pure computation). The input is a stock (`market` and `ticker`, price from the catalog) or a manual `price` with its `currency`, and exactly one of `amount` or `shares`. The rate comes from a new `MarketData.Contracts` port over the converter of ADR-0028, so the path, day and source of the rate travel with the answer, with a flag when it is stale. A stale or pending stock price is reported, never hidden. The optional spread or fee assumption of the spec is not part of this milestone: how a fee is applied to an amount is ambiguous and is decided when asked for.

## Consequences
- The arithmetic is small and fully covered by property tests, so the application part only wires inputs.
- Shares and money may differ from a broker's: its rate, fees and minimum order sizes are its own. The answer says it is a reference.
- `FsCheck.Xunit` is now in the central package list and available to every test project.
