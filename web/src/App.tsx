import { useState, type FormEvent } from 'react';
import { ChatThread } from './components/ChatThread';
import { PersonaSwitcher } from './components/PersonaSwitcher';
import { SuggestedQuestions } from './components/SuggestedQuestions';
import { useChat } from './hooks/use-chat';
import { DEFAULT_PERSONA, PERSONAS } from './lib/personas';

const MAX_MESSAGE = 2000; // the API rejects longer messages

export function App() {
  const { state, persona, auth, selectPersona, retryAuth, send, stop, busy } = useChat(DEFAULT_PERSONA);
  const [draft, setDraft] = useState('');
  const current = PERSONAS.find((p) => p.id === persona)!;
  const ready = auth.status === 'ready';

  function submit(e: FormEvent) {
    e.preventDefault();
    if (!ready || busy || !draft.trim()) return;
    void send(draft);
    setDraft('');
  }

  return (
    <div className="mx-auto flex h-dvh max-w-4xl flex-col px-4">
      <header className="flex flex-wrap items-start justify-between gap-3 py-4">
        <div>
          <h1 className="text-lg font-semibold">SQL Agent</h1>
          <p className="text-xs text-slate-500">Ask in plain language. The SQL, the rows and every refusal are shown.</p>
        </div>
        <PersonaSwitcher persona={persona} auth={auth} onSelect={selectPersona} onRetry={retryAuth} />
      </header>

      <main className="min-h-0 flex-1 overflow-y-auto pb-4">
        {state.turns.length === 0 ? (
          <SuggestedQuestions persona={current} disabled={!ready} onPick={(q) => void send(q)} />
        ) : (
          <ChatThread turns={state.turns} />
        )}
      </main>

      <form onSubmit={submit} className="flex gap-2 border-t border-slate-200 py-3">
        <input
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          maxLength={MAX_MESSAGE}
          placeholder={ready ? 'Ask a question…' : 'Waiting for sign-in…'}
          aria-label="Question"
          className="min-w-0 flex-1 rounded-lg border border-slate-300 bg-white px-3 py-2 outline-none focus:border-sky-500"
        />
        {busy ? (
          <button type="button" onClick={stop} className="rounded-lg bg-slate-700 px-4 py-2 text-white hover:bg-slate-800">
            Stop
          </button>
        ) : (
          <button type="submit" disabled={!ready || !draft.trim()} className="rounded-lg bg-sky-600 px-4 py-2 text-white hover:bg-sky-700 disabled:opacity-50">
            Send
          </button>
        )}
      </form>
    </div>
  );
}
