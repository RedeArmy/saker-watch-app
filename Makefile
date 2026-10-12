COMPOSE=docker compose -p saker-watch -f deploy/docker-compose.yml --profile dev
.PHONY: up-no-avx up down logs test format check-docs
up:        ## start dev infrastructure (Postgres+Timescale, Mongo, Redis, Caddy, fake IdP)
	$(COMPOSE) up -d
up-no-avx: ## same as up, with Mongo 4.4 for CPUs without AVX
	$(COMPOSE) -f deploy/docker-compose.no-avx.yml up -d
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
	bash scripts/check-todos.sh
