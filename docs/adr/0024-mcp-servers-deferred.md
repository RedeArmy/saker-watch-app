# ADR-0024: Project MCP servers are deferred; `.mcp.json` stays empty

- **Status:** accepted
- **Date:** 2026-10-15
- **Deciders:** Eder

## Context
ADR-0002 planned a shared `.mcp.json` for Phase 1 with GitHub, PostgreSQL and MongoDB servers, the databases through read-only roles on the development instances only and credentials only through `${ENV}` expansion. Phase 1 is now complete apart from its gate, and the compose stack, the roles (`api_role`, `worker_role`) and the migrator exist. Three things argue against adding the servers now:

- **Supply chain.** Each server is third-party code that runs on the developer machine with the credentials we give it. The popular PostgreSQL reference server was archived by its maintainers; the GitHub and MongoDB servers are actively maintained but ship often. The project pins every action by SHA and every package by verified version (ADR-0019, ADR-0020); an MCP server would need the same discipline and an update path.
- **No read-only login exists.** The development logins created by `scripts/dev-create-logins.sh` are the runtime logins. A read-only login that cannot see another user's rows and cannot read `platform.audit_log` payloads would need its own role design, which belongs with the data that is worth querying (Phase 2).
- **Little to gain today.** The development databases hold only test identities and platform bookkeeping. The GitHub CLI and the owner's reviews already cover issues and pull requests.

## Options considered
1. Add the three servers now with read-only roles. Rejected for the reasons above.
2. Add only the GitHub server. Rejected: it needs a token with repository scope on every developer machine for little benefit over `gh`.
3. Keep `.mcp.json` empty and decide per server, with an ADR each, when there is a concrete need. **Chosen.**

## Decision
`.mcp.json` remains `{"mcpServers": {}}`. A server is added only by a new ADR that names: the package and an exact verified version, the credentials it receives (read-only, development instance only, `${ENV}` expansion, never committed), what it can reach, and how it is updated. A PostgreSQL or MongoDB server also needs a dedicated read-only login created by a script, with a test that it cannot write and cannot cross users. This revises the Phase 1 mention in ADR-0002 and the roadmap; the target phase of the decision moves to Phase 2 (when market data gives the databases content worth inspecting).

## Consequences
No third-party code with database or repository credentials runs in the development workflow. Developers inspect data with `psql`/`mongosh` through the compose stack. The owner can overrule this at the Phase 1 gate; the checklist above is the work to do then.
