import type { SseEvent, SseEventName } from './types';

/** One raw frame: the `event:` name and the joined `data:` lines. Comment lines (`: ping`) never produce a frame. */
export interface SseFrame {
  event: string;
  data: string;
}

/**
 * Splits a byte stream into SSE frames. Chunks can end anywhere (inside a line, between the `\r` and `\n` of a line
 * break, inside a multi-byte character), so bytes are decoded as a stream and lines are only handled once complete.
 * A frame ends at a blank line; a stream that ends mid-frame drops the partial frame (it was never completed).
 */
export async function* parseSseStream(body: ReadableStream<Uint8Array>): AsyncGenerator<SseFrame> {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  let event = '';
  let data: string[] = [];

  const lineEnd = /\r\n|\n|\r/;

  try {
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });

      for (;;) {
        const match = lineEnd.exec(buffer);
        if (!match) break;
        // A trailing "\r" may be the first half of "\r\n": wait for the next chunk before deciding.
        if (match[0] === '\r' && match.index === buffer.length - 1) break;

        const line = buffer.slice(0, match.index);
        buffer = buffer.slice(match.index + match[0].length);

        if (line === '') {
          if (data.length > 0 || event !== '') yield { event: event || 'message', data: data.join('\n') };
          event = '';
          data = [];
        } else if (line.startsWith(':')) {
          // Comment (heartbeat): carries no data.
        } else {
          const colon = line.indexOf(':');
          const field = colon === -1 ? line : line.slice(0, colon);
          let val = colon === -1 ? '' : line.slice(colon + 1);
          if (val.startsWith(' ')) val = val.slice(1);
          if (field === 'event') event = val;
          else if (field === 'data') data.push(val);
        }
      }
    }
  } finally {
    reader.releaseLock();
  }
}

const KNOWN: ReadonlySet<string> = new Set<SseEventName>([
  'session', 'trace', 'tool_call', 'query_result', 'query_error', 'guardrail_blocked', 'text', 'usage', 'error', 'done',
]);

/** A typed event, or null for a frame that is not part of the contract or whose data is not a JSON object. */
export function toSseEvent(frame: SseFrame): SseEvent | null {
  if (!KNOWN.has(frame.event)) return null;
  try {
    const data: unknown = frame.data === '' ? {} : JSON.parse(frame.data);
    if (typeof data !== 'object' || data === null) return null;
    return { event: frame.event, data } as SseEvent;
  } catch {
    return null;
  }
}

/** The typed events of a response body, in order. Unknown or malformed frames are skipped. */
export async function* readEvents(body: ReadableStream<Uint8Array>): AsyncGenerator<SseEvent> {
  for await (const frame of parseSseStream(body)) {
    const event = toSseEvent(frame);
    if (event) yield event;
  }
}
