import type { CellValue, QueryColumn, SseEvent, Violation } from '../api/types';

/** The fixed answer the server substitutes when it cannot stand behind the model's text (AgentTurnRunner.FallbackMessage). */
export const FALLBACK_ANSWER_PREFIX = "I couldn't answer this reliably";

export type CallOutcome = 'running' | 'result' | 'query_error' | 'blocked';

/** One tool call of a turn, built up from `tool_call` and the event that settles it, paired by `callId`. */
export interface Call {
  callId: string;
  tool: string;
  sql?: string;
  purpose?: string;
  outcome: CallOutcome;
  result?: { columns: QueryColumn[]; rows: CellValue[][]; rowCount: number; truncated: boolean; elapsedMs: number };
  errorMessage?: string;
  violations?: Violation[];
}

export type TurnStatus = 'streaming' | 'done' | 'error' | 'aborted';

export interface Turn {
  id: string;
  question: string;
  status: TurnStatus;
  /** In event order, so several queries in one turn render in the order they happened. */
  calls: Call[];
  /** `text` events: the answer, then possibly a warning note about figures that match no result. */
  texts: string[];
  traceId?: string;
  usage?: { inputTokens: number; outputTokens: number };
  errorMessage?: string;
  /** The server discards cancelled, failed and fallback-answer turns: the conversation continues without them. */
  notSaved: boolean;
}

export interface ChatState {
  conversationId?: string;
  turns: Turn[];
}

export type ChatAction =
  | { type: 'send'; id: string; question: string }
  | { type: 'event'; event: SseEvent }
  | { type: 'ended' }
  | { type: 'failed'; message: string }
  | { type: 'abort' }
  | { type: 'drop_conversation' }
  | { type: 'reset' };

export const initialChatState: ChatState = { turns: [] };

export const isStreaming = (state: ChatState) => state.turns.at(-1)?.status === 'streaming';

function updateLast(state: ChatState, update: (turn: Turn) => Turn): ChatState {
  const last = state.turns.at(-1);
  if (!last || last.status !== 'streaming') return state;
  return { ...state, turns: [...state.turns.slice(0, -1), update(last)] };
}

/** Sets the call with this id, creating it if its `tool_call` was never seen. */
function upsertCall(turn: Turn, callId: string, patch: Partial<Call>): Turn {
  const exists = turn.calls.some((c) => c.callId === callId);
  const calls = exists
    ? turn.calls.map((c) => (c.callId === callId ? { ...c, ...patch } : c))
    : [...turn.calls, { callId, tool: 'run_sql', outcome: 'running' as const, ...patch }];
  return { ...turn, calls };
}

function applyEvent(state: ChatState, event: SseEvent): ChatState {
  if (event.event === 'session') return { ...state, conversationId: event.data.conversationId };

  return updateLast(state, (turn) => {
    switch (event.event) {
      case 'trace':
        return { ...turn, traceId: event.data.traceId };
      case 'tool_call': {
        const { callId, tool, sql, purpose } = event.data;
        return upsertCall(turn, callId, { tool, sql, purpose });
      }
      case 'query_result': {
        const { callId, sql, ...result } = event.data;
        return upsertCall(turn, callId, { sql, outcome: 'result', result });
      }
      case 'query_error':
        return upsertCall(turn, event.data.callId, { sql: event.data.sql, outcome: 'query_error', errorMessage: event.data.message });
      case 'guardrail_blocked':
        return upsertCall(turn, event.data.callId, { sql: event.data.sql, outcome: 'blocked', violations: event.data.violations });
      case 'text':
        return { ...turn, texts: [...turn.texts, event.data.delta] };
      case 'usage':
        return { ...turn, usage: event.data };
      case 'error':
        return { ...turn, status: 'error', errorMessage: event.data.message, notSaved: true };
      case 'done':
        return { ...turn, status: 'done', notSaved: turn.texts[0]?.startsWith(FALLBACK_ANSWER_PREFIX) ?? false };
      default:
        return turn;
    }
  });
}

export function chatReducer(state: ChatState, action: ChatAction): ChatState {
  switch (action.type) {
    case 'send':
      return {
        ...state,
        turns: [...state.turns, { id: action.id, question: action.question, status: 'streaming', calls: [], texts: [], notSaved: false }],
      };
    case 'event':
      return applyEvent(state, action.event);
    case 'ended':
      // The body closed without `done`: never leave the turn spinning.
      return updateLast(state, (t) => ({
        ...t,
        status: 'error',
        errorMessage: 'The connection ended before the answer was complete.',
        notSaved: true,
      }));
    case 'failed':
      return updateLast(state, (t) => ({ ...t, status: 'error', errorMessage: action.message, notSaved: true }));
    case 'abort':
      return updateLast(state, (t) => ({ ...t, status: 'aborted', notSaved: true }));
    case 'drop_conversation':
      return { ...state, conversationId: undefined };
    case 'reset':
      return initialChatState;
  }
}
