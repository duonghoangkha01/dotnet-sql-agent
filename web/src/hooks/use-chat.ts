import { useCallback, useEffect, useReducer, useRef, useState } from 'react';
import { ApiError, fetchDemoToken, openChatStream } from '../api/chat-api';
import { readEvents } from '../api/sse-client';
import type { PersonaId } from '../api/types';
import { chatReducer, initialChatState, isStreaming } from './chat-reducer';

export type AuthState = { status: 'loading' } | { status: 'ready' } | { status: 'error'; message: string };

const errorText = (e: unknown) => (e instanceof Error ? e.message : 'Something went wrong.');

/**
 * Chat state plus the network around it. The JWT lives in a ref (memory only). Switching persona aborts any running
 * turn, drops the conversation and gets a new token; the server enforces conversation ownership regardless.
 */
export function useChat(initialPersona: PersonaId) {
  const [state, dispatch] = useReducer(chatReducer, initialChatState);
  const [persona, setPersona] = useState(initialPersona);
  const [auth, setAuth] = useState<AuthState>({ status: 'loading' });
  const [authAttempt, setAuthAttempt] = useState(0);

  const token = useRef<string | null>(null);
  const run = useRef<AbortController | null>(null);
  const stateRef = useRef(state);
  stateRef.current = state;
  const personaRef = useRef(persona);
  personaRef.current = persona;

  // One token per persona selection. The cleanup cancels a request that a quick switch has made stale.
  useEffect(() => {
    const ctrl = new AbortController();
    token.current = null;
    setAuth({ status: 'loading' });
    fetchDemoToken(persona, ctrl.signal)
      .then((t) => {
        token.current = t.token;
        setAuth({ status: 'ready' });
      })
      .catch((e: unknown) => {
        if (!ctrl.signal.aborted) setAuth({ status: 'error', message: errorText(e) });
      });
    return () => ctrl.abort();
  }, [persona, authAttempt]);

  const selectPersona = useCallback((next: PersonaId) => {
    if (next === personaRef.current) return;
    run.current?.abort();
    run.current = null;
    // Synchronously, so nothing can be sent with the previous persona's token in the render before the effect runs.
    token.current = null;
    setAuth({ status: 'loading' });
    dispatch({ type: 'reset' });
    setPersona(next);
  }, []);

  const retryAuth = useCallback(() => setAuthAttempt((n) => n + 1), []);

  const send = useCallback(async (message: string) => {
    const text = message.trim();
    // run.current is set before the first await, so a double click or Enter plus Send cannot start two turns.
    if (!text || run.current || isStreaming(stateRef.current) || !token.current) return;

    const ctrl = new AbortController();
    run.current = ctrl;
    dispatch({ type: 'send', id: crypto.randomUUID(), question: text });

    // Opens the stream, recovering from the three answers that are not the user's fault.
    async function open() {
      let request = { conversationId: stateRef.current.conversationId, message: text };
      let refreshed = false;
      for (let busyRetries = 0; ; ) {
        try {
          return await openChatStream(token.current!, request, ctrl.signal);
        } catch (e) {
          if (!(e instanceof ApiError)) throw e;
          if (e.status === 401 && !refreshed) {
            // Token expired: get a new one for the same persona.
            refreshed = true;
            token.current = (await fetchDemoToken(personaRef.current, ctrl.signal)).token;
          } else if (e.status === 404 && request.conversationId) {
            // The conversation expired or was evicted: ask the same question in a new one.
            dispatch({ type: 'drop_conversation' });
            request = { conversationId: undefined, message: text };
          } else if (e.status === 409 && busyRetries++ < 4) {
            // A turn the user just stopped is still being torn down on the server.
            await new Promise((r) => setTimeout(r, 500));
          } else {
            throw e;
          }
        }
      }
    }

    try {
      const body = await open();
      for await (const event of readEvents(body)) {
        if (ctrl.signal.aborted) return;
        dispatch({ type: 'event', event });
      }
      if (!ctrl.signal.aborted) dispatch({ type: 'ended' });
    } catch (e) {
      if (!ctrl.signal.aborted) dispatch({ type: 'failed', message: errorText(e) });
    } finally {
      if (run.current === ctrl) run.current = null;
    }
  }, []);

  const stop = useCallback(() => {
    run.current?.abort();
    run.current = null;
    dispatch({ type: 'abort' });
  }, []);

  useEffect(() => () => run.current?.abort(), []);

  return { state, persona, auth, selectPersona, retryAuth, send, stop, busy: isStreaming(state) };
}
