# PR-FAQ — Saker Watch

- **Purpose:** working-backwards press release and FAQ.
- **Owner:** Eder · **Status:** in-review · **Target phase:** 0

## Press release (draft)
**Saker Watch helps self-directed investors in Guatemala decide which stocks deserve their research time.** Keep a watchlist, see price changes across eight windows, convert USD to GTQ and learn what a partial share costs. A nightly analysis agent, running on a self-hosted model, reads each stock's data and a transparent rules engine flags it *Interesting*, *Neutral* or *Avoid* — a research prioritization aid, never financial advice. Saker Watch never trades and never connects to a broker.

## FAQ
- **Does it tell me to buy?** No. Signals only prioritize your own research. Every screen carries a disclaimer.
- **Who computes the signal?** A deterministic, versioned rules engine, not the LLM; reasons are stored and shown.
- **Is my data private?** Per-user isolation at app and database level; no PII/tokens in logs.
- **Why GTQ?** The primary user earns in GTQ; FX date and source are always shown.
- **What does it cost to run?** Oracle Always Free plus one self-hosted LLM server; no paid SaaS required.
- **What is still open?** Provider licensing for multi-user use, trademark clearance, LLM hardware and which Asian exchanges to add; see `docs/assumptions.md`.
