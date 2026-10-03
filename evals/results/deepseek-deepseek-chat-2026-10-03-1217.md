# Eval: deepseek-chat (deepseek), 2026-10-03

**Single run.** Execution accuracy on 0 golden questions: **n/a**.
A run is one pass at temperature 0 with a fixed seed where the provider honors one: it can differ on another run (see evals/README.md).

## Run

| | |
|---|---|
| Provider / model | deepseek / deepseek-chat |
| Quantization | n/a |
| Provider version | OpenAI 2.14.0.0 via api.deepseek.com |
| Code | f0e3239f3072 + uncommitted changes |
| Temperature / seed | 0 / 42 |
| Context window | provider default |
| Result visibility | Summary |
| Model-call cap per question | 6 |
| Started / finished (UTC) | 2026-10-03 12:17 / 2026-10-03 12:18 |

## Accuracy

| Set | Passed |
|---|---|
| All (without holdout) | n/a |
| simple | n/a |
| multi-join | n/a |
| time-window | n/a |
| Holdout (reported separately) | 18/18 (100.0%) |

## Behavior

| | |
|---|---|
| Guardrail false positives (reference queries refused) | 0/18 (0.0%), target under 5% |
| Guardrail refusals of the agent's own queries | 0 |
| Iteration-cap hits | 0 |
| Model calls per question (mean) | 3.9 |
| run_sql calls per question (mean) | 1.0 |
| Tokens per question (mean, in / out) | 8479 / 262 |
| Latency p50 / p95 | 3,6 s / 4,2 s |
| Cost per question | not computed (no price given; a local model has none) |

