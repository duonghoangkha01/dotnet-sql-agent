// Mirrors docs/sse-contract.md (and SseEvent.cs) exactly. Property names are camelCase; null properties are omitted.

export interface QueryColumn {
  name: string;
  type: string;
}

export type CellValue = string | number | boolean | null;

export interface Violation {
  code: string;
  message: string;
}

export type SseEvent =
  | { event: 'session'; data: { conversationId: string } }
  | { event: 'trace'; data: { traceId: string } }
  | { event: 'tool_call'; data: { callId: string; tool: string; sql?: string; purpose?: string } }
  | {
      event: 'query_result';
      data: {
        callId: string;
        sql: string;
        columns: QueryColumn[];
        rows: CellValue[][];
        rowCount: number;
        truncated: boolean;
        elapsedMs: number;
      };
    }
  | { event: 'query_error'; data: { callId: string; sql: string; message: string } }
  | { event: 'guardrail_blocked'; data: { callId: string; sql: string; violations: Violation[] } }
  | { event: 'text'; data: { delta: string } }
  | { event: 'usage'; data: { inputTokens: number; outputTokens: number } }
  | { event: 'error'; data: { message: string } }
  | { event: 'done'; data: Record<string, never> };

export type SseEventName = SseEvent['event'];

export const PERSONA_IDS = ['demo-sales-rep-nw', 'demo-finance', 'demo-admin'] as const;
export type PersonaId = (typeof PERSONA_IDS)[number];

export interface DemoToken {
  token: string;
  expiresInSeconds: number;
  persona: { id: string; role: string; territoryId?: number; displayName: string };
}
