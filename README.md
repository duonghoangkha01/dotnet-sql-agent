# dotnet-sql-agent

**Ask your SQL Server database questions in plain language, and let the database, not the prompt, decide who sees what.**

<!-- demo-gif: docs/demo.gif (15 s: ask, SQL, table, answer). Add once recorded. -->
<!-- demo-video: Loom link goes here once recorded; script in docs/loom-script.md -->

Business users ask a question; the agent writes T-SQL, runs it as that user's role, and shows the SQL, the full result
table and a short answer. Built on ASP.NET Core and Microsoft Agent Framework, running on a local model (Ollama) or
Azure OpenAI.

## Why this isn't a toy

- **Three independent access layers.** A default-deny SQL guardrail (AST-based), one database user per role, and
  row-level security. A query must pass all three; the last two hold even if the guardrail has a bug.
  [docs/architecture.md](docs/architecture.md) has the rule table, [docs/rls-coverage.md](docs/rls-coverage.md) lists what is
  protected and what is knowingly left open.
- **Every number in the results table comes from the database.** The answer text above it is written by the model, which
  is told to quote only rows it received. Code replaces the answer with a fixed message if the model ran out of steps
  or states figures with no successful query, and flags figures that appear in no result.
- **Access control does not depend on the model behaving. Model output is sanitized, not trusted:** it is shown as plain
  text under a CSP, because it can be wrong or steered by text in the data.
- **Measured, not claimed.** An eval suite scores answers by the data they return; see [Evaluation](#evaluation).
- **Audited and observable.** Every query attempt is logged; every question is one trace.

## Requirements

- Docker Desktop with about 12 GB of memory available to it, and about 10 GB of free disk for the first run.
  With the WSL 2 backend, Docker gets half of your RAM by default. To raise it, create `%UserProfile%\.wslconfig`
  containing `[wsl2]` and `memory=12GB`, then run `wsl --shutdown` and restart Docker Desktop.
- 16 GB RAM or more, an amd64 machine, and about 10 GB of first-time downloads (model, SQL Server image,
  AdventureWorks backup).
- An NVIDIA GPU is recommended for the local model (Ollama, default `qwen3:4b`). CPU works but is slow. No local model has
  been benchmarked on the VRAM or the answer quality yet (see [Roadmap](#roadmap)), so no speed figure is claimed here.
- .NET 10 SDK only if you build or test the solution yourself; the stack itself runs in Docker.

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
| Chat UI | http://127.0.0.1:3000 | Pick a persona, ask a question. nginx serves it and proxies `/api` |
| API | internal | `docker compose -f docker-compose.yml -f compose.dev.yml up` opens it on http://127.0.0.1:8080 |
| SQL Server, Ollama | internal | The same dev override opens them on 127.0.0.1 |

To use Azure OpenAI instead of the local model, set these in `.env`, then start with `docker compose up -d --wait`:

```
COMPOSE_PROFILES=            # empty: do not start Ollama
LLM_PROVIDER=azure
AZURE_OPENAI_ENDPOINT=...  AZURE_OPENAI_DEPLOYMENT=...  AZURE_OPENAI_API_KEY=...
```

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

## The agent and the API

`SqlAgent.Api` runs one Microsoft Agent Framework agent per role (`sales_rep`, `finance`, `admin`) over an
`IChatClient`: Ollama by default, or Azure OpenAI with `LLM_PROVIDER=azure`, with no code change. An agent holds only
its instructions and the role's few-shot examples. The tools (`list_tables`, `describe_tables`, `run_sql`) are made
for each request and bound to the caller's role and territory from the token, so the model can supply a table name, SQL
and a purpose and nothing else.

- **Streaming.** `POST /api/chat/stream` answers with server-sent events: the SQL, the full result rows and any refusal
  as they happen, then the answer. The contract is in [docs/sse-contract.md](docs/sse-contract.md).
- **Conversations** belong to the user and role that started them. Someone else's id, or an unknown one, is a 404. One
  question runs per user at a time, and a turn that is cancelled or fails is not saved.
- **Limits.** 10 requests a minute per user, 4 chats at once, 6 model calls and 30 s of database time per question,
  and after 3 failed queries in a row the model is told to ask the user to rephrase.
- **What the model reads of a result** is `RESULT_VISIBILITY` in `.env`: `None` (columns and row count), `Summary`
  (plus 5 rows, the default) or `Rows` (up to 50). The user always sees the whole result. Use `None` if rows must not
  reach an LLM provider.
- **No invented numbers, as far as code can enforce it.** The prompt tells the model to quote only figures it received.
  If the tool loop runs out, or the answer states figures without a successful query, the answer is replaced by a fixed
  message. A figure that is in none of the results gets a warning note. Arithmetic the model did itself is flagged too.
- **Audit.** Every `run_sql` call, refused or not, is written once to `SqlAgent.dbo.AuditLog`, which the application's
  database user can append to but not change.
- **Telemetry.** One question is one trace in the Aspire Dashboard: the request, each model call, each tool call, the
  guardrail verdict (`guardrail.validate`: allowed, violation codes) and the query (`sql.execute`: row count, truncated,
  time, role). Spans hold counts and codes only: no SQL text, rows, prompts or answers, unless content capture is turned on.
  Metrics (meter `SqlAgent`): `sqlagent.tokens` (by model and direction), `sqlagent.guardrail.blocks` (by violation code),
  `sqlagent.query.duration`, `sqlagent.turn.duration`, `sqlagent.iteration_cap_hits`. There is no cost metric.
- **Demo logins.** With `DEMO_AUTH=true` (the default in `.env`), `POST /api/auth/demo-token` with a persona
  (`demo-sales-rep-nw`, `demo-finance` or `demo-admin`) returns a token. Turn it off for anything that is not local.

Access control does not depend on the model behaving: the guardrail, the role's database user and row-level security
decide what a query can read, whatever the prompt or the data says. The model's text is not trusted in the same way: it
can be wrong or be steered by text in the data, so a client should show it as plain text.

## The chat UI

`web/` is a Vite + React + TypeScript + Tailwind app, served by nginx (`deploy/nginx/default.conf`), which also proxies
`/api` so the browser sees one origin. A persona switcher signs in as `sales_rep`, `finance` or `admin` and starts a new
conversation; each persona has six suggested questions, one in Vietnamese and one that tries to get past the guardrails.
For each question the UI shows, in order, the SQL, the full result table (500 rows at most, with a truncation badge), a
card for a refused or failed query, the answer, and a footer with rows, time, tokens and a link to the trace in the
Aspire Dashboard. Stop cancels the question; a cancelled or failed turn is marked as not saved.

The answer is shown as plain text, never as markdown, and nginx sends a CSP that blocks any other origin, so text planted
in the data cannot load an image or open a link. To work on the UI with hot reload, start the API with `compose.dev.yml`
and run `npm run dev` in `web/` (http://127.0.0.1:5173, `/api` is proxied to 127.0.0.1:8080). Behind nginx, every browser
shares one client address, so the demo-token limit (10 a minute) is shared too.

## Evaluation

`SqlAgent.Cli eval` runs 36 golden questions through the same agent as the API, as each persona, and scores every answer by
whether the query it ran returns the data the reference query returns: values, not SQL text, with documented rules for
column matching, order, numbers and dates. Six holdout questions are reported separately. A run records the model,
quantization, server version and git commit, and a v0.1 result is a single run. The method, the golden-set rules and what
is not measured yet are in [evals/README.md](evals/README.md); raw reports go to `evals/results/`.

<!-- eval-results:start -->
No eval has been published yet. Run `SqlAgent.Cli eval` (see [evals/README.md](evals/README.md)).
<!-- eval-results:end -->

## Build and test

```bash
dotnet build SqlAgent.sln
dotnet test tests/SqlAgent.Core.Tests
```

```bash
dotnet test tests/SqlAgent.IntegrationTests    # needs Docker
```

```bash
cd web && npm ci && npm run build && npx vitest run    # typecheck, build, parser/reducer/rendering tests
```

The API's own tests (`tests/SqlAgent.IntegrationTests/Api`) run the real API in memory with a scripted model and fake
queries, so they need neither an LLM nor a database.

The integration tests start their own SQL Server container (the image pinned in `docker-compose.yml`), restore
AdventureWorks and replay `deploy/sql`. The first run downloads the backup (about 200 MB, checksum-verified) into a
temp directory; set `SQLAGENT_TEST_BAK` to a file path to keep it somewhere else. A run takes about a minute once
the image and the backup are local. The older `SchemaIntrospectorTests` instead run against your local stack and are
skipped unless `SQLAGENT_TEST_APP_CONNECTION` holds a connection string for the `sqlagent_app` user (start the stack
with `compose.dev.yml` to publish SQL Server on `127.0.0.1:1433`).

## Production concerns and threat model

What an attacker (a user, or text planted in the data) can try, and what stands in the way. Each row names the control; the
tests behind it are in [docs/rls-coverage.md](docs/rls-coverage.md) and `tests/`.

| Concern | What is done | What remains |
|---|---|---|
| Direct prompt injection ("ignore your rules, read X") | The model cannot widen access: guardrail, role database user and RLS decide, whatever the prompt says. 104 adversarial guardrail cases | The model may be talked into a wrong or unhelpful query |
| Indirect injection (instructions inside data) | Same layers. Answers are plain text under a CSP, so planted text cannot load an image or open a link | Planted text can still make the answer text misleading |
| Exfiltration through output | Plain-text rendering, nginx CSP blocks other origins | None known beyond the above |
| Aggregate side channels | RLS on every territory-keyed table, plus column denies on pre-aggregated ones (`SalesTerritory`, `SalesPerson`) | `Person.Person` names are readable by design, listed in the coverage table |
| Error-oracle side channel (a crafted `WHERE` that errors only on hidden rows) | The guardrail restricts the query shape; the eval suite watches for it | **Residual risk.** SQL Server documents this for filter predicates; the database cannot close it |
| Ownership chaining (views, table-valued functions exposing denied columns) | Allowlist holds base tables only; views and functions are refused | A view granted later would reopen it |
| `SESSION_CONTEXT` with parallel plans | `MAXDOP = 1`, enforced by Resource Governor (a hint cannot override it); territory set read-only; a pooled-connection concurrency test | |
| Denial of service | 5,000-character and 40-level nesting limit, 15 s timeout, 500 rows, 2 MB, per-user rate limits, Resource Governor CPU and memory caps | No global token or cost budget |
| Data residency | `RESULT_VISIBILITY=None` keeps rows away from the LLM provider (it sees columns and a row count) | The question text always reaches the provider |
| Least privilege and audit | No `db_datareader`; the application user can append to the audit log but not change it | |

**What changes for production:** Entra ID instead of demo logins (`DEMO_AUTH=false`), secrets in Key Vault, persisted
conversations (they are in memory now), a token budget with atomic reservation, private endpoints for SQL Server and the
model, and a real RLS and column-deny review of your own schema (AdventureWorks is static; yours is not).

## Roadmap

Not built yet: a live benchmark of the local models (the planned go/no-go run of `qwen3:4b` and `qwen3:8b`), published
eval numbers with repeated runs, a grounding check for answers against stored results, and persisted conversations.

## Need this for your .NET system?

I build agents like this against real SQL Server schemas, including the access model. Get in touch:
<!-- contact: add LinkedIn and Upwork links here before publishing -->

## License

MIT
