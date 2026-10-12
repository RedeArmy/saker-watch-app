# Saker Watch

Personal stock watchlist and research assistant (no trading, no broker). See `docs/pr-faq.md` and `docs/rfc/0001-design-doc.md`.

## Prerequisites (Ubuntu)
```bash
sudo apt update && sudo apt install -y dotnet-sdk-10.0 jq make git pipx gh
pipx install pre-commit
dotnet tool install -g dotnet-ef            # add ~/.dotnet/tools to PATH
# gitleaks: download the release tarball from github.com/gitleaks/gitleaks/releases
#   -> extract, then: sudo install gitleaks /usr/local/bin/
# Docker + Compose and Node 24 are also required.
pre-commit install
```

## 10-minute local setup
```bash
cp deploy/.env.example deploy/.env   # fill dev-only values
make up                              # Postgres+Timescale, Mongo, Redis, Caddy (http://localhost:8088), fake IdP
dotnet build -warnaserror && dotnet test
make check-docs
```
To run the API/Worker from your IDE, also publish the datastores on loopback (warning: this override makes the `backend` network non-internal; use it for local development only):
`docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.dev-ports.yml --profile dev up -d`.

On CPUs/VMs without AVX (`mongo:7.0` crashes with exit 132), add `-f deploy/docker-compose.no-avx.yml` to the compose command or run `make up-no-avx` (assumption A-013). For tests on such machines set `SAKER_TEST_MONGO_IMAGE=mongo:4.4`.

## Documentation
`docs/product-spec.md` (what and why) · `docs/roadmap.md` (phases and gates) · `docs/rfc/0001-design-doc.md` · `docs/adr/` (decisions) · `docs/data-model.md` · `docs/threat-model/` · `docs/observability/` · `docs/api/` · `docs/assumptions.md` · `docs/clavaris-contract.md` · `docs/glossary.md` · `docs/DOCS-INDEX.md` (status of every document) · `docs/runbooks/sonarcloud-setup.md` (quality gates and branch protection).

Product UI language: Spanish (`es-GT`). Code and docs: English. Informational only — not financial advice.
