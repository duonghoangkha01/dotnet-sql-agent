# Evals

How well does the agent answer business questions, measured so that the number can be checked? `SqlAgent.Cli eval` runs
the golden set through the same agent the API runs and scores each answer by the data it returned.

```bash
docker compose -f docker-compose.yml -f compose.dev.yml up -d --wait    # SQL Server and Ollama on 127.0.0.1
dotnet run --project src/SqlAgent.Cli -c Release -- eval                # all questions, seed 42
dotnet run --project src/SqlAgent.Cli -c Release -- eval --only simple-01-order-count,time-04-running-total-2012
dotnet run --project src/SqlAgent.Cli -c Release -- eval --update-readme   # also refresh the README table
```

Settings are read from the environment, then from `.env`: `LLM_PROVIDER`, `OLLAMA_MODEL`, `OLLAMA_CONTEXT_LENGTH`,
`DEEPSEEK_*`, `AZURE_OPENAI_*`, `SQL_*_PASSWORD`, `RESULT_VISIBILITY`. Without `SQL_SERVER` and `OLLAMA_ENDPOINT` it uses 127.0.0.1, where
`compose.dev.yml` publishes them. Evals need a model, so they run by hand before a README update and never in CI.

## What runs

The eval builds the agent from Core exactly as the API does: the same `RoleAgentRegistry`, system prompt, `semantic.yaml`,
tools, guardrail and `SafeQueryExecutor`, as the same per-role database users with row-level security. Only the surroundings
differ: no audit log (`NullAuditSink`), a capturing event sink instead of an HTTP stream, and a row cap of 10,000 instead
of 500 in the guardrail and the executor, so that a reference result and an agent result are compared in full. Each
question starts a fresh conversation. A fix for a failing question goes into the prompt or `semantic.yaml`, so the API gets it
too.

## Scoring: execution accuracy

An item passes when the **last query that ran successfully before the final answer** returns what the item's reference
query returns. The text of the SQL is not compared. `ResultSetComparer` (`src/SqlAgent.Core/Evals`) decides, and its tests
are the specification:

1. **The row counts are equal.** A result cut at the 10,000-row cap never passes.
2. **Column matching is value-based and greedy.** Every reference column must match a distinct agent column holding the
   same multiset of values. Column names, aliases and column order do not matter. Extra agent columns are allowed, because
   the row count already matches.
3. **The matched columns must also agree row by row.** The same combinations of values, not just the same values per column:
   a result that pairs each name with another row's total fails even though each column alone is right. (This is stricter
   than matching columns alone, because that would pass a wrong pairing.) Where several agent columns hold identical
   values, the assignment that makes the rows line up is used.
4. **Row order is ignored unless the item has `ordered: true`.** Ranking items are written without ties at the boundary,
   so there is only one right order; this was checked against the data.

Values are compared as follows:

| Kind | Rule |
|---|---|
| Numbers | Equal within 1e-6, across numeric types (`int`, `decimal`, `float`, ...); `bit` counts as 0 and 1 |
| Dates and times | By value whatever the SQL type: a `date` equals a `datetime` at midnight; offsets are converted to UTC |
| Text | Exactly equal, case included, except trailing spaces |
| NULL | Equal to NULL only |
| Number vs text | Never equal: `5` is not `'5'` |

A turn that ends in the fallback message ("I couldn't answer this reliably"), with a model error, or with no successful
query fails, whatever queries ran. A golden item whose own reference fails, returns no rows or exceeds the row cap is
reported as **not scorable** and left out of every figure; it is fixed in the golden set.

### What is recorded per item

Verdict, the reason for a failure, model calls (counted from the item's own trace), `run_sql` calls, guardrail refusals
of the agent's queries, query errors, tokens, latency, the scored SQL and the answer text. No result rows are stored; the answer text can quote a few figures from them, and the reference data is AdventureWorks sample data.

**Guardrail false positives** are measured on the known-good side: the share of reference queries that the guardrail would
refuse. The reference is still run, as the persona's database user, so the item is scored. The target is under 5%. Refusals of
the *agent's* queries are listed separately, because most are the guardrail doing its job on a wrong query.

**Iteration-cap hit**: the turn fell back to the fixed message with every allowed model call used.

## The golden set

`golden.json`: 36 items, 18 `simple`, 12 `multi-join`, 6 `time-window`, over the three personas (the sales rep is the
Northwest territory, so their answers differ from finance and admin by row-level security). Fields: `id`, `persona`
(`demo-sales-rep-nw`, `demo-finance`, `demo-admin`), `question`, `referenceSql`, `tier`, `ordered`, `holdout`.

- Every reference is run as its persona's own database user by the integration test `GoldenSetTests`, which also requires
  that the guardrail accepts it, that it returns between 1 and 10,000 rows and that it was not cut. A reference can therefore
  never contain data its persona may not see.
- A question says what the result should hold when two readings are equally natural ("only count products that belong to a
  subcategory", "show the month number"). Execution accuracy cannot tell a wrong answer from a different but fair one, so
  the wording carries that burden.
- **The golden set is never edited to make a run pass.** A failing question is fixed in the prompt or `semantic.yaml`.
- Items with `"holdout": true` (6 of them) are never looked at while tuning, and are reported on their own. If a holdout item
  was used to tune, it is no longer a holdout: say so in the report.

## Determinism

Temperature 0 and a seed (`LLM_SEED`, default 42 for evals) where the provider honors one. That makes a run repeatable in
practice, not guaranteed: GPU kernels and server versions can change the result. Every report records the model,
quantization, Ollama (or SDK) version, context window, result visibility, the model-call cap and the git commit (with a
note if the working tree had changes). **A v0.1 run is a single run**, and every table labels it so. The spread between
runs is not measured yet.

## Output

`evals/results/<provider>-<model>-<date>.md` and `.json` (a second run on the same day gets a time suffix). The JSON is the
record; the Markdown and the README table (between the `eval-results` markers in the root README) are generated from it.
`--readme-only` regenerates the table from the newest result of each provider and model.

Cost per question is computed once, in the report, and only when you pass a published list price with its date
(`--price-in 2 --price-out 8 --price-date 2026-10-01`, USD per million tokens). A local model has none. Nothing in the running
system accrues cost.

## Not in this version

The second provider (Azure OpenAI), three runs per question with mean and range, the adversarial prompt set with its
leak oracle, the ambiguous-question tier scored by a rubric, and a number-grounding score are planned for v1.0.
