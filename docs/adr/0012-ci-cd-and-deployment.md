# ADR-0012: CI/CD and deployment

- **Status:** accepted
- **Date:** 2026-10-12

## Decision
GitHub Actions: restore → build (warnings as errors) → format → unit → integration → architecture → coverage → CodeQL → gitleaks → dependency/container scan (Trivy) → SBOM (CycloneDX) → multi-arch images (linux/arm64 included) → EF migration bundle + Mongo migration artifact. Deploy to Oracle Always Free (Ampere A1) via Docker Compose behind Caddy: backup → migrate → start → smoke → rollback. Immutable image tags (git SHA). Only Caddy is public; DBs on a private network.
