COMPOSE=docker compose -p saker-watch -f deploy/docker-compose.yml --profile dev
.PHONY: up down logs test format check-docs
up:        ## start dev infrastructure (Postgres+Timescale, Mongo, Redis, Caddy, fake IdP)
	$(COMPOSE) up -d
down:
	$(COMPOSE) down
logs:
	$(COMPOSE) logs -f
test:
	dotnet test
format:
	dotnet format
check-docs:
	bash scripts/check-docs-index.sh
