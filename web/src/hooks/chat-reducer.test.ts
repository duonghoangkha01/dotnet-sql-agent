import { describe, expect, it } from 'vitest';
import type { SseEvent } from '../api/types';
import { chatReducer, FALLBACK_ANSWER_PREFIX, initialChatState, isStreaming, type ChatAction, type ChatState } from './chat-reducer';

const run = (actions: ChatAction[], from: ChatState = initialChatState) => actions.reduce(chatReducer, from);
const ev = (event: SseEvent): ChatAction => ({ type: 'event', event });
const send = (question = 'q'): ChatAction => ({ type: 'send', id: crypto.randomUUID(), question });

const result = (callId: string, sql = 'SELECT 1'): SseEvent => ({
  event: 'query_result',
  data: { callId, sql, columns: [{ name: 'n', type: 'int' }], rows: [[1]], rowCount: 1, truncated: false, elapsedMs: 5 },
});

describe('chatReducer', () => {
  it('takes the conversation id from the session event and keeps it for follow-ups', () => {
    const state = run([send(), ev({ event: 'session', data: { conversationId: 'c1' } }), ev({ event: 'done', data: {} }), send('again')]);
    expect(state.conversationId).toBe('c1');
    expect(state.turns).toHaveLength(2);
    expect(state.turns[0]!.status).toBe('done');
    expect(isStreaming(state)).toBe(true);
  });

  it('shows several queries in one turn in event order, each paired by callId', () => {
    const state = run([
      send(),
      ev({ event: 'tool_call', data: { callId: 'a', tool: 'run_sql', sql: 'SELECT 1', purpose: 'first' } }),
      ev({ event: 'tool_call', data: { callId: 'b', tool: 'run_sql', sql: 'SELECT 2', purpose: 'second' } }),
      ev(result('b', 'SELECT TOP (501) 2')),
      ev(result('a', 'SELECT TOP (501) 1')),
    ]);
    const calls = state.turns[0]!.calls;
    expect(calls.map((c) => c.callId)).toEqual(['a', 'b']);
    expect(calls[0]).toMatchObject({ purpose: 'first', outcome: 'result', sql: 'SELECT TOP (501) 1' });
    expect(calls[1]).toMatchObject({ purpose: 'second', outcome: 'result', sql: 'SELECT TOP (501) 2' });
  });

  it('keeps a run_sql call running until its outcome arrives', () => {
    const state = run([send(), ev({ event: 'tool_call', data: { callId: 'a', tool: 'run_sql', sql: 'SELECT 1' } })]);
    expect(state.turns[0]!.calls[0]!.outcome).toBe('running');
  });

  it('records query_error and guardrail_blocked on their calls', () => {
    const state = run([
      send(),
      ev({ event: 'tool_call', data: { callId: 'a', tool: 'run_sql', sql: 'SELECT bad' } }),
      ev({ event: 'query_error', data: { callId: 'a', sql: 'SELECT bad', message: 'Invalid column' } }),
      ev({ event: 'tool_call', data: { callId: 'b', tool: 'run_sql', sql: 'DROP TABLE x' } }),
      ev({ event: 'guardrail_blocked', data: { callId: 'b', sql: 'DROP TABLE x', violations: [{ code: 'NOT_SELECT', message: 'Only SELECT.' }] } }),
    ]);
    const [a, b] = state.turns[0]!.calls;
    expect(a).toMatchObject({ outcome: 'query_error', errorMessage: 'Invalid column' });
    expect(b).toMatchObject({ outcome: 'blocked', violations: [{ code: 'NOT_SELECT', message: 'Only SELECT.' }] });
  });

  it('creates a call when its result arrives without a tool_call', () => {
    const state = run([send(), ev(result('x'))]);
    expect(state.turns[0]!.calls).toHaveLength(1);
    expect(state.turns[0]!.calls[0]).toMatchObject({ callId: 'x', tool: 'run_sql', outcome: 'result' });
  });

  it('collects the answer, a warning note, the trace id and usage, then finishes saved', () => {
    const state = run([
      send(),
      ev({ event: 'trace', data: { traceId: 'abc' } }),
      ev({ event: 'text', data: { delta: 'Sales were 12.' } }),
      ev({ event: 'text', data: { delta: 'Note: 99 is not in the results.' } }),
      ev({ event: 'usage', data: { inputTokens: 10, outputTokens: 20 } }),
      ev({ event: 'done', data: {} }),
    ]);
    expect(state.turns[0]).toMatchObject({
      status: 'done',
      traceId: 'abc',
      texts: ['Sales were 12.', 'Note: 99 is not in the results.'],
      usage: { inputTokens: 10, outputTokens: 20 },
      notSaved: false,
    });
  });

  it('marks a turn that ends with the fixed fallback answer as not saved', () => {
    const state = run([send(), ev({ event: 'text', data: { delta: `${FALLBACK_ANSWER_PREFIX} — please rephrase.` } }), ev({ event: 'done', data: {} })]);
    expect(state.turns[0]).toMatchObject({ status: 'done', notSaved: true });
  });

  it('an error event settles the turn as error and not saved, and the done after it does not undo that', () => {
    const state = run([send(), ev({ event: 'error', data: { message: 'model failed' } }), ev({ event: 'done', data: {} })]);
    expect(state.turns[0]).toMatchObject({ status: 'error', errorMessage: 'model failed', notSaved: true });
  });

  it('a stream that ends without done becomes an error, never stuck streaming', () => {
    const state = run([send(), ev({ event: 'text', data: { delta: 'partial' } }), { type: 'ended' }]);
    expect(state.turns[0]).toMatchObject({ status: 'error', notSaved: true });
    expect(isStreaming(state)).toBe(false);
  });

  it('an ended stream after done changes nothing', () => {
    const state = run([send(), ev({ event: 'done', data: {} }), { type: 'ended' }]);
    expect(state.turns[0]!.status).toBe('done');
  });

  it('abort marks the turn aborted and not saved, and later events are ignored', () => {
    const state = run([send(), ev({ event: 'trace', data: { traceId: 't' } }), { type: 'abort' }, ev({ event: 'text', data: { delta: 'late' } }), ev({ event: 'done', data: {} })]);
    expect(state.turns[0]).toMatchObject({ status: 'aborted', notSaved: true, texts: [] });
  });

  it('a failed request becomes an error on the pending turn', () => {
    const state = run([send(), { type: 'failed', message: 'Too many requests.' }]);
    expect(state.turns[0]).toMatchObject({ status: 'error', errorMessage: 'Too many requests.', notSaved: true });
  });

  it('keeps the conversation after an aborted turn so the next question continues it', () => {
    const state = run([send(), ev({ event: 'session', data: { conversationId: 'c1' } }), { type: 'abort' }]);
    expect(state.conversationId).toBe('c1');
  });

  it('drop_conversation forgets the id but keeps the turns; reset clears everything', () => {
    const base = run([send(), ev({ event: 'session', data: { conversationId: 'c1' } }), ev({ event: 'done', data: {} })]);
    expect(chatReducer(base, { type: 'drop_conversation' })).toMatchObject({ conversationId: undefined, turns: [expect.anything()] });
    expect(chatReducer(base, { type: 'reset' })).toEqual(initialChatState);
  });
});
