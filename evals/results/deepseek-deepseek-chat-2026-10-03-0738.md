# Eval: deepseek-chat (deepseek), 2026-10-03

**Single run.** Execution accuracy on 30 golden questions: **30/30 (100.0%)**.
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
| Started / finished (UTC) | 2026-10-03 07:38 / 2026-10-03 07:41 |

## Accuracy

| Set | Passed |
|---|---|
| All (without holdout) | 30/30 (100.0%) |
| simple | 15/15 (100.0%) |
| multi-join | 10/10 (100.0%) |
| time-window | 5/5 (100.0%) |
| persona demo-admin | 9/9 (100.0%) |
| persona demo-finance | 14/14 (100.0%) |
| persona demo-sales-rep-nw | 7/7 (100.0%) |
| Holdout (reported separately) | 6/6 (100.0%) |

## Behavior

| | |
|---|---|
| Guardrail false positives (reference queries refused) | 0/36 (0.0%), target under 5% |
| Guardrail refusals of the agent's own queries | 0 |
| Iteration-cap hits | 0 |
| Model calls per question (mean) | 3.9 |
| run_sql calls per question (mean) | 1.0 |
| Tokens per question (mean, in / out) | 8483 / 234 |
| Latency p50 / p95 | 3,6 s / 4,3 s |
| Cost per question | not computed (no price given; a local model has none) |

