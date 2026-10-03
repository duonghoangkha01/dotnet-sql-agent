# Demo video script (about 2 minutes)

The published video is https://youtu.be/P_z_GM-F4Xg (AI voice-over). This table is the outline it follows.

Record at 1080p with the stack already running and both browser tabs warm (the first model call is a cold start).
Have two sessions ready: the chat UI on http://127.0.0.1:3000 and the Aspire Dashboard on http://127.0.0.1:18888.
Do not show `.env`, a terminal with secrets, or any window other than these two. Pre-run each question once so the model
is loaded, then reset the conversation before recording.

| Time | Screen | Say |
|---|---|---|
| 0:00 | Chat UI, Finance persona | "Business people can't write T-SQL, and giving an LLM a database login is how data leaks. This agent lets people ask in plain language and keeps the database in charge of who sees what." |
| 0:15 | Ask: *total sales by territory last year* | "Finance asks a plain question. The agent writes the SQL, you see it, you see the full result table, then a short answer. The numbers in that table come from the database, not from the model." |
| 0:50 | Switch persona to Sales Rep, ask the same question | "Same question, Sales Rep. The numbers change: this role only sees its own territory, Northwest. That is enforced by the database, not by the prompt." |
| 1:20 | Sales Rep, click the suggested attack question | "Now someone tries to get past it. The query is refused before it reaches the database, and the reason is in the card." |
| 1:40 | Aspire Dashboard, open the trace from the footer link | "Every question is one trace: the model calls, the guardrail verdict, the query. It records counts and codes, not your data." |
| 2:05 | README eval table | "And it's measured: 48 golden questions, scored by whether the data matches a reference query. On DeepSeek, one run: 18 of 18 on the questions I never tuned against, which is a small sample, so the README gives a lower bound of 82%. The 30 questions I did tune against score 30 of 30; the README says which is which." |
| 2:30 | README top section | "It runs locally on one command, with a DeepSeek key or Azure OpenAI, or a local model. If you need this for your .NET system, my LinkedIn and Upwork are at the bottom of the README." |

Say only numbers the README eval table shows. The video is linked at the top of the README.
