# ADR-0001: Product naming and IP note

- **Status:** accepted
- **Date:** 2026-10-12
- **Deciders:** Eder

## Context
The product was first called "Kestrel". That name collides with an existing stock-research product, a pending financial-software trademark application and ASP.NET Core's Kestrel web server.

## Options considered
1. Kestrel — rejected (collisions above).
2. Saker Watch — a quick search showed no direct finance/software conflict.

## Decision
Product **Saker Watch**; repo `saker-watch-app`; root namespace `SakerWatch`; docker prefix / compose project `saker-watch`.

## Consequences
- **No formal trademark clearance has been done.** Before any public or commercial release: search USPTO (classes 9, 36, 42), WIPO Global Brand Database, GitHub/NuGet/npm and domains, and consult a trademark attorney (tracked in `docs/assumptions.md`, A-001).
- Neutral-association caveats: a listed company (Saker Aviation Services) and a well-known political blog use "Saker". Keep branding and SEO in mind if this goes public.
