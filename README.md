# dotnet-sql-agent

A natural-language-to-SQL agent for SQL Server, built on ASP.NET Core and Microsoft Agent Framework. Business
users ask questions in plain language; the agent writes T-SQL, and three layers decide what it can read: a
default-deny SQL guardrail, one database user per role, and row-level security. Each layer has its own
failure modes; [docs/rls-coverage.md](docs/rls-coverage.md) lists what is protected and what is knowingly left open.

**Status: work in progress.** Only the local infrastructure exists so far. This README grows with the code.

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

## Build

```bash
dotnet build SqlAgent.sln
```

## License

MIT
