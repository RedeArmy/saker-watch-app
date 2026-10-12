# ADR-0008: LLM analysis pipeline and deterministic signals

- **Status:** accepted
- **Date:** 2026-10-12

## Decision
- Self-hosted **Ollama** (OpenAI-compatible API) on a separate GPU host reached over WireGuard/Tailscale + service token; never public. Hardware TBD (assumptions A-004). Behind `ILlmAnalyzer`; deterministic `FakeLlm` for tests/CI.
- The LLM has no tools/side effects. Provider text is untrusted data (delimited, length-capped, injection-neutralized). Output validated against versioned JSON Schema, one repair retry, else run `failed`.
- **Scores and signals are computed by the deterministic rules engine** (`ISignalRule` composed by a versioned DB rule set), never by the LLM. Every signal stores machine-readable `reasons`.
- Prompts are versioned, checksummed data; initial templates are `draft` with `TODO(owner: Eder)` where the rubric is incomplete; thresholds are configuration.
- Analyses are shared per stock + prompt version + date.

## Consequences
Eval harness (golden set 20–30 stocks) must run before changing model/prompt.
