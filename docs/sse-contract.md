# Chat API and SSE contract

This is the contract between `SqlAgent.Api` and its clients. The event records in
[`SseEvent.cs`](../src/SqlAgent.Core/Agent/SseEvent.cs) define the payloads; the web client's types mirror them.

## Authentication

`POST /api/auth/demo-token` with `{"persona": "<id>"}` returns `{token, expiresInSeconds, persona}`. It exists only when
`DEMO_AUTH=true` (otherwise 404) and is rate-limited per client address. The personas have fixed `sub` values, so a
persona always owns the same conversations:

| Persona id | Role | Territory |
|---|---|---|
| `demo-sales-rep-nw` | `sales_rep` | 1 |
| `demo-finance` | `finance` | all |
| `demo-admin` | `admin` | all |

Send the token as `Authorization: Bearer <token>`. The token is a JWT (HS256) signed with `JWT_SIGNING_KEY`; the
role and territory in it are the only source of what a query runs as. Nothing the model writes can change them.

## `POST /api/chat/stream`

Request body: `{"conversationId": "<guid>"?, "message": "<text, 1-2000 characters>"}`.
Omit `conversationId` to start a conversation. The server generates the id and returns it in the first event.

Problems that can be decided before the stream starts are ordinary HTTP responses with `{"error": "..."}`:

| Status | Meaning |
|---|---|
| 400 | Empty or over-long `message`. |
| 401 | Missing, invalid or expired token. |
| 404 | The conversation id is malformed, unknown, expired, or belongs to another user or role. These are not told apart. |
| 409 | The user already has a question running, or this conversation does. |
| 429 | More than 10 requests a minute for this user, or the server is running its maximum number of chats (4). `Retry-After` is set for the rate limit. |

Otherwise the response is `200 text/event-stream`.

## Events

Each event is `event: <name>` and `data: <json>` followed by a blank line. Property names are camelCase and null
properties are omitted. A line starting with `:` is a comment (`: ping`, sent every 15 s while idle) and carries no data.

| Event | Payload | Notes |
|---|---|---|
| `session` | `{conversationId}` | Always first. |
| `trace` | `{traceId}` | Right after `session`. Open it in the Aspire Dashboard at `/traces/detail/<traceId>`. |
| `tool_call` | `{callId, tool, sql?, purpose?}` | `tool` is `list_tables`, `describe_tables` or `run_sql`. `sql` and `purpose` only for `run_sql`. `sql` is what the model wrote. |
| `query_result` | `{callId, sql, columns, rows, rowCount, truncated, elapsedMs}` | May repeat within a turn. `callId` pairs it with its `tool_call`. `sql` is the text that ran (with the row cap added). `columns` is `[{name, type}]`; `rows` is an array per row. `truncated` means more rows exist than were returned (at most 500 rows or about 2 MB are returned). |
| `query_error` | `{callId, sql, message}` | The database rejected the query or it timed out. |
| `guardrail_blocked` | `{callId, sql, violations}` | The guardrail refused the query; it never reached the database. `violations` is `[{code, message}]`. |
| `text` | `{delta}` | The answer. See below. |
| `usage` | `{inputTokens, outputTokens}` | Tokens only; there is no cost estimate. |
| `error` | `{message}` | The model call failed. The message is generic. |
| `done` | `{}` | Always last, as long as the client is still connected. |

A normal turn is `session`, `trace`, then any number of `tool_call` / `query_result` / `query_error` /
`guardrail_blocked`, then `text`, `usage`, `done`.

### The answer text

Text is not streamed token by token. The server holds the model's text until the model has finished, because whether
the answer may be shown depends on the whole turn:

- Text the model writes before a tool call is discarded; it is thinking aloud, not the answer.
- If the model ran out of its 6 calls, or gave no answer, or stated figures without having run a successful query, the
  answer is replaced by the fixed text *I couldn't answer this reliably — please rephrase or narrow the question.*
- If the answer states a figure that appears in none of the turn's results (nor in the user's question), a second `text`
  event follows with a warning note. Figures are matched across formatting and rounding ("1,234.5", "1.234,5",
  "about 1.2 million"). Arithmetic the model did itself (a sum, a percentage of its own) is reported too.

Tool events are sent as soon as they happen, so the UI can show the SQL and rows while the model is still working.

### A turn that is not saved

A turn is added to the conversation only when it completes. A turn that is cancelled (the client disconnected), that
fails (`error`), or whose answer was replaced by the fixed text is discarded: the conversation continues from where it
was before the turn. A UI should mark such a turn as not saved.

## Limits

| What | Limit |
|---|---|
| Requests | 10 a minute per user (`CHAT_RATE_LIMIT_PER_MINUTE`) |
| Running at once | 1 per user, 4 in all (`MAX_CONCURRENT_RUNS`) |
| Conversations | 5 per user, 200 in all, 30 minutes idle; the oldest makes room for a new one |
| History kept | about 6,000 tokens (estimated), oldest turns dropped whole |
| Model calls per question | 6 |
| Database time per question | 30 s in all, 15 s per query; after 3 failed or refused queries in a row the model is told to ask the user to rephrase |
| What the model sees of a result | `RESULT_VISIBILITY`: `None` (columns, row count), `Summary` (plus 5 rows, the default) or `Rows` (up to 50). The `query_result` event always has the full rows. |

## Audit

Each `run_sql` call, allowed or refused, is written once to `SqlAgent.dbo.AuditLog` after it finished: user, role,
conversation, question, SQL, whether it was allowed, the violations, rows returned, time taken and trace id. The
application's database user cannot update or delete these rows. If the write fails it is logged, and the turn is not
failed: the query has already run.
