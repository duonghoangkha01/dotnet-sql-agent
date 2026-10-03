import type { DemoToken, PersonaId } from './types';

/** The API base is always the relative /api: nginx (production) and the Vite proxy (dev) both serve it same-origin. */
const API = '/api';

/** A non-2xx response. `message` is the server's `{error}` text when it sent one. */
export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
  ) {
    super(message);
  }
}

async function failure(res: Response): Promise<ApiError> {
  let message = `Request failed (${res.status}).`;
  try {
    const body = (await res.json()) as { error?: unknown };
    if (typeof body.error === 'string' && body.error) message = body.error;
  } catch {
    // Not JSON (a proxy error page, say): keep the generic message.
  }
  const retryAfter = res.headers.get('Retry-After');
  if (res.status === 429 && retryAfter) message += ` Try again in ${retryAfter} s.`;
  return new ApiError(res.status, message);
}

export async function fetchDemoToken(persona: PersonaId, signal?: AbortSignal): Promise<DemoToken> {
  const res = await fetch(`${API}/auth/demo-token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ persona }),
    signal,
  });
  if (!res.ok) throw await failure(res);
  return (await res.json()) as DemoToken;
}

/** Starts a chat turn and returns the SSE body. Problems decided before the stream starts throw an ApiError. */
export async function openChatStream(
  token: string,
  request: { conversationId?: string; message: string },
  signal: AbortSignal,
): Promise<ReadableStream<Uint8Array>> {
  const res = await fetch(`${API}/chat/stream`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}`, Accept: 'text/event-stream' },
    body: JSON.stringify(request),
    signal,
  });
  if (!res.ok) throw await failure(res);
  if (!res.body) throw new ApiError(res.status, 'The server sent no response body.');
  return res.body;
}
