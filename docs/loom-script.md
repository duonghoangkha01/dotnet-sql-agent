# Loom script (2-3 minutes)

Record at 1080p with the stack already running and both browser tabs warm (the first model call is a cold start).
Have two sessions ready: the chat UI on http://127.0.0.1:3000 and the Aspire Dashboard on http://127.0.0.1:18888.
Do not show `.env`, a terminal with secrets, or any window other than these two. Pre-run each question once so the model
is loaded, then reset the conversation before recording.

| Time | Screen | Say |
|---|---|---|
| 0:00 | Chat UI, Finance persona | "Business people can't write T-SQL, and giving an LLM a database login is how data leaks. This agent lets people ask in plain language and keeps the database in charge of who sees what." |
| 0:15 | Ask: *total sales by territory last year* | "Finance asks a plain question. The agent writes the SQL, you see it, you see the full result table, then a short answer. The numbers in that table come from the database, not from the model." |
| 0:50 | Switch persona to Sales Rep, ask the same question | "Same question, Sales Rep. Far fewer rows: only their own territory. That is enforced by the database, not by the prompt." |
| 1:20 | Sales Rep, click the suggested attack question | "Now someone tries to get past it. The query is refused before it reaches the database, and the reason is in the card." |
| 1:40 | Aspire Dashboard, open the trace from the footer link | "Every question is one trace: the model calls, the guardrail verdict, the query. It records counts and codes, not your data." |
| 2:05 | README eval table | "And it's measured: golden questions scored by whether the data matches a reference query, with the method and the failures published." |
| 2:30 | README top section | "It runs locally on one command, or against Azure OpenAI with three settings. If you need this for your .NET system, the contact is at the bottom of the README." |

Before recording, replace the README eval table placeholder by running the eval, and say only numbers that table shows.
Link the finished video in the README (section "Demo").
