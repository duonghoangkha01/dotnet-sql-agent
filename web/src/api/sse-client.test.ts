import { describe, expect, it } from 'vitest';
import { parseSseStream, readEvents, toSseEvent, type SseFrame } from './sse-client';

const encoder = new TextEncoder();

/** A body that delivers exactly these chunks, as a network would. */
function body(chunks: (string | Uint8Array)[]): ReadableStream<Uint8Array> {
  return new ReadableStream({
    start(controller) {
      for (const c of chunks) controller.enqueue(typeof c === 'string' ? encoder.encode(c) : c);
      controller.close();
    },
  });
}

async function collect<T>(gen: AsyncGenerator<T>): Promise<T[]> {
  const out: T[] = [];
  for await (const item of gen) out.push(item);
  return out;
}

const frames = (chunks: (string | Uint8Array)[]) => collect<SseFrame>(parseSseStream(body(chunks)));

describe('parseSseStream', () => {
  it('parses a whole frame', async () => {
    expect(await frames(['event: session\ndata: {"conversationId":"c1"}\n\n'])).toEqual([
      { event: 'session', data: '{"conversationId":"c1"}' },
    ]);
  });

  it('reassembles a frame split at every position', async () => {
    const text = 'event: text\ndata: {"delta":"hi"}\n\nevent: done\ndata: {}\n\n';
    for (let cut = 1; cut < text.length; cut++) {
      const result = await frames([text.slice(0, cut), text.slice(cut)]);
      expect(result.map((f) => f.event)).toEqual(['text', 'done']);
    }
  });

  it('handles one byte per chunk, including a multi-byte character split across chunks', async () => {
    const bytes = encoder.encode('event: text\ndata: {"delta":"Tổng doanh thu"}\n\n');
    const result = await frames(Array.from(bytes, (b) => new Uint8Array([b])));
    expect(JSON.parse(result[0]!.data)).toEqual({ delta: 'Tổng doanh thu' });
  });

  it('joins multi-line data with a newline', async () => {
    expect(await frames(['event: text\ndata: line one\ndata: line two\n\n'])).toEqual([
      { event: 'text', data: 'line one\nline two' },
    ]);
  });

  it('ignores ping comments, between and inside frames', async () => {
    const result = await frames([': ping\n\n', 'event: trace\n: ping\ndata: {"traceId":"t"}\n\n', ': ping\n']);
    expect(result).toEqual([{ event: 'trace', data: '{"traceId":"t"}' }]);
  });

  it('accepts CRLF, including a CR and LF in different chunks', async () => {
    const result = await frames(['event: done\r', '\ndata: {}\r\n\r', '\n']);
    expect(result).toEqual([{ event: 'done', data: '{}' }]);
  });

  it('drops a frame the stream ended in the middle of', async () => {
    expect(await frames(['event: done\ndata: {}\n\nevent: text\ndata: {"del'])).toEqual([{ event: 'done', data: '{}' }]);
  });

  it('keeps a data value that has no space after the colon', async () => {
    expect(await frames(['event:done\ndata:{}\n\n'])).toEqual([{ event: 'done', data: '{}' }]);
  });
});

describe('toSseEvent', () => {
  it('types a known event', () => {
    expect(toSseEvent({ event: 'usage', data: '{"inputTokens":3,"outputTokens":4}' })).toEqual({
      event: 'usage',
      data: { inputTokens: 3, outputTokens: 4 },
    });
  });

  it('treats an empty data as an empty object (done)', () => {
    expect(toSseEvent({ event: 'done', data: '' })).toEqual({ event: 'done', data: {} });
  });

  it('skips unknown events and malformed or non-object data', () => {
    expect(toSseEvent({ event: 'surprise', data: '{}' })).toBeNull();
    expect(toSseEvent({ event: 'text', data: '{oops' })).toBeNull();
    expect(toSseEvent({ event: 'text', data: '"a string"' })).toBeNull();
    expect(toSseEvent({ event: 'text', data: 'null' })).toBeNull();
  });
});

describe('readEvents', () => {
  it('yields typed events in order and skips frames outside the contract', async () => {
    const events = await collect(
      readEvents(body(['event: session\ndata: {"conversationId":"c"}\n\n', 'event: nope\ndata: {}\n\n', ': ping\n\n', 'event: done\ndata: {}\n\n'])),
    );
    expect(events.map((e) => e.event)).toEqual(['session', 'done']);
  });

  it('ends without a done event when the stream does (the caller must treat that as an error)', async () => {
    const events = await collect(readEvents(body(['event: session\ndata: {"conversationId":"c"}\n\n'])));
    expect(events.map((e) => e.event)).toEqual(['session']);
  });
});
