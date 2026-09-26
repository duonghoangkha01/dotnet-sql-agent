# dotnet-sql-agent

A natural-language-to-SQL agent for SQL Server, built on ASP.NET Core and Microsoft Agent Framework. Business
users ask questions in plain language; the agent writes T-SQL, and three layers decide what it can read: a
default-deny SQL guardrail, one database user per role, and row-level security. Each layer has its own
failure modes; [docs/rls-coverage.md](docs/rls-coverage.md) lists what is protected and what is knowingly left open.

**Status: work in progress.** So far there is the local infrastructure and the schema catalog with its semantic
layer; the guardrail, the agent and the UI are not built yet. This README grows with the code.

## Requirements

- Docker Desktop with about 12 GB of memory available to it, and about 10 GB of free disk for the first run.
  With the WSL 2 backend, Docker gets half of your RAM by default. To raise it, create `%UserProfile%\.wslconfig`
  containing `[wsl2]` and `memory=12GB`, then run `wsl --shutdown` and restart Docker Desktop.
- An NVIDIA GPU is recommended for the local model (Ollama). CPU works but is slow.
- .NET 10 SDK to build the solution.

## Run the local stack

```bash
scripts/init-env.sh        # once. On Windows PowerShell: scripts/init-env.ps1
docker compose -f docker-compose.yml -f compose.gpu.yml up -d --wait     # NVIDIA GPU
# or: docker compose up -d --wait                                        # CPU only
```

The first start downloads about 200 MB of AdventureWorks, restores it, creates the database users and the
row-level-security policy, and only then reports the database healthy. `.env` holds generated secrets and is
never committed.

| Service | Address | Notes |
|---|---|---|
| Aspire Dashboard (traces, metrics) | http://127.0.0.1:18888 | Local only. Open a trace with `/traces/detail/<traceId>` |
| SQL Server, Ollama | internal | `docker compose -f docker-compose.yml -f compose.dev.yml up` opens them on 127.0.0.1 |

To use Azure OpenAI instead of the local model, set `COMPOSE_PROFILES=` (empty) and `LLM_PROVIDER=azure` in `.env`.

## Check the infrastructure

```bash
scripts/verify-infra.sh                    # add --restart-check to also test a second boot
```

It verifies row-level security for each role, denial of writes and of restricted columns, and that nothing is
reachable beyond `127.0.0.1`. See [docs/rls-coverage.md](docs/rls-coverage.md) for what is protected and how.

## What each role may read

[`semantic.yaml`](src/SqlAgent.Core/assets/semantic.yaml) is the single source of truth for what each role
(`sales_rep`, `finance`, `admin`) can see: which tables, which columns are denied, plus table descriptions,
join hints and few-shot examples. The agent's schema tools, the guardrail's allowlist and the database grants
are all built from it, so they cannot disagree. A table is denied to a role by being absent from that role's
list, and a typo in the file fails startup with the key path of the mistake.

The database grants are generated. After editing `semantic.yaml`, regenerate them and commit the result; CI
fails if they are out of date:

```bash
dotnet run --project src/SqlAgent.Cli -- emit-grants     # rewrites deploy/sql/40-role-grants.generated.sql
```

The generated script is applied on every boot of the SQL Server container. To apply it to a running stack:
`docker compose exec sqlserver /bin/bash /scripts/apply-scripts.sh`.

## Build and test

```bash
dotnet build SqlAgent.sln
dotnet test tests/SqlAgent.Core.Tests
```

The tests in `tests/SqlAgent.IntegrationTests` need the local database and are skipped unless
`SQLAGENT_TEST_APP_CONNECTION` holds a connection string for the `sqlagent_app` user (start the stack with
`compose.dev.yml` to publish SQL Server on `127.0.0.1:1433`).

## License

MIT
