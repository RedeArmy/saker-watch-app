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
To run the API/Worker from your IDE, also publish the datastores on loopback:
`docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.dev-ports.yml --profile dev up -d`.

Known dev-environment caveat: `mongo:7.0` needs AVX; VMs without AVX (see `docs/assumptions.md` A-013) must use another Mongo version or host.

Product UI language: Spanish (`es-GT`). Code and docs: English. Informational only — not financial advice.
