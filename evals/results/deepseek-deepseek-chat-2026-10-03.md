# Eval: deepseek-chat (deepseek), 2026-10-03

**Single run.** Execution accuracy on 30 golden questions: **26/30 (86.7%)**.
A run is one pass at temperature 0 with a fixed seed where the provider honors one: it can differ on another run (see evals/README.md).

## Run

| | |
|---|---|
| Provider / model | deepseek / deepseek-chat |
| Quantization | n/a |
| Provider version | OpenAI 2.14.0.0 via api.deepseek.com |
| Code | a485bc9bb2b3 + uncommitted changes |
| Temperature / seed | 0 / 42 |
| Context window | provider default |
| Result visibility | Summary |
| Model-call cap per question | 6 |
| Started / finished (UTC) | 2026-10-03 07:34 / 2026-10-03 07:36 |

## Accuracy

| Set | Passed |
|---|---|
| All (without holdout) | 26/30 (86.7%) |
| simple | 14/15 (93.3%) |
| multi-join | 8/10 (80.0%) |
| time-window | 4/5 (80.0%) |
| persona demo-admin | 9/9 (100.0%) |
| persona demo-finance | 14/14 (100.0%) |
| persona demo-sales-rep-nw | 3/7 (42.9%) |
| Holdout (reported separately) | 6/6 (100.0%) |

## Behavior

| | |
|---|---|
| Guardrail false positives (reference queries refused) | 0/36 (0.0%), target under 5% |
| Guardrail refusals of the agent's own queries | 0 |
| Iteration-cap hits | 0 |
| Model calls per question (mean) | 3.9 |
| run_sql calls per question (mean) | 1.2 |
| Tokens per question (mean, in / out) | 8379 / 278 |
| Latency p50 / p95 | 3,3 s / 6,4 s |
| Cost per question | not computed (no price given; a local model has none) |

## Failures

| Id | Result | Why |
|---|---|---|
| simple-13-my-distinct-customers | WrongResult | row count differs: expected 1, got 10 |
| multi-07-my-top-products | WrongResult | row count differs: expected 5, got 10 |
| multi-09-my-revenue-per-category | WrongResult | row count differs: expected 4, got 1 |
| time-03-my-busiest-month-2013 | WrongResult | row count differs: expected 1, got 10 |

