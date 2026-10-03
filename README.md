# dotnet-sql-agent

A natural-language-to-SQL agent for SQL Server, built on ASP.NET Core and Microsoft Agent Framework. Business
users ask questions in plain language; the agent writes T-SQL, and three layers decide what it can read: a
default-deny SQL guardrail, one database user per role, and row-level security. Each layer has its own
failure modes; [docs/rls-coverage.md](docs/rls-coverage.md) lists what is protected and what is knowingly left open.

**Status: work in progress.** So far there is the local infrastructure, the schema catalog with its semantic
layer, and the SQL guardrail with its safe executor; the agent and the UI are not built yet. This README grows
with the code.

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

## The SQL guardrail

Every query the model writes goes through `SqlGuardrail` before it reaches the database. It parses the text with
Microsoft's T-SQL parser (ScriptDom) and is default-deny: a syntax element that is not on the allowlist is refused,
so a construct nobody thought of fails closed.

- Exactly one `SELECT` (a CTE may precede it). No `INTO`, `FOR XML/JSON`, table or query hints, `OPTION`, variables
  or `@@` globals.
- Tables: only the role's own (two-part `Schema.Table` names), derived tables, joins, and CTEs defined earlier in the
  same query. No functions, `OPENROWSET`/`OPENQUERY`, views, temp tables or other databases.
- Functions: aggregates, window, string, date and math only. `*` only inside `COUNT(*)`.
- Columns a role may not read are refused wherever they appear (select list, `WHERE`, `ORDER BY`, subqueries, CTEs),
  with aliases resolved per query. Where that cannot be told, an unqualified name is refused if *any* table in scope
  denies it: refusing too much is the safe error, and the database denies the column as well.
- Text longer than 5,000 characters or nested deeper than 40 parentheses is refused. ScriptDom recurses per level and a
  stack overflow cannot be caught, so about 1,000 nested parentheses would end the process.

An approved query gets `TOP (501)` when its outermost query allows it, and what runs is the SQL regenerated from the
checked tree, so what was checked is what runs. Refusals say what to change and name only what the query itself
contained; a denied table and a missing one get the same reply. `SafeQueryExecutor` then runs the query as the
role's database user, with the territory fixed in a read-only session context, a 15 s limit, a 500-row cap and a
2 MB result-size cap (`Truncated` says more exists either way). The size cap matters on its own: `STRING_AGG` or any
aggregate with no `GROUP BY` returns the whole allowed table in a single row, which a row count alone would not
catch. The database adds its own limits per reader (`deploy/sql/45-resource-governor.sql`): no parallelism, a
CPU-time ceiling and a memory-grant cap.

Tests: 104 adversarial cases, each refused with a named reason (`tests/SqlAgent.Core.Tests/Guardrails`), 48 legitimate
queries plus every shipped example that must pass, and integration tests against a real SQL Server for row-level
security, the executor and the Resource Governor. Validation takes about 0.65 ms at the median (1,000 validations of
typical analytical queries on the development machine).

## Build and test

```bash
dotnet build SqlAgent.sln
dotnet test tests/SqlAgent.Core.Tests
```

```bash
dotnet test tests/SqlAgent.IntegrationTests    # needs Docker
```

The integration tests start their own SQL Server container (the image pinned in `docker-compose.yml`), restore
AdventureWorks and replay `deploy/sql`. The first run downloads the backup (about 200 MB, checksum-verified) into a
temp directory; set `SQLAGENT_TEST_BAK` to a file path to keep it somewhere else. A run takes about a minute once
the image and the backup are local. The older `SchemaIntrospectorTests` instead run against your local stack and are
skipped unless `SQLAGENT_TEST_APP_CONNECTION` holds a connection string for the `sqlagent_app` user (start the stack
with `compose.dev.yml` to publish SQL Server on `127.0.0.1:1433`).

## License

MIT
