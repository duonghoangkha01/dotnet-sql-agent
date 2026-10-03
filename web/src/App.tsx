import { useState } from 'react';
import { AppHeader } from './components/AppHeader';
import { ChatThread } from './components/ChatThread';
import { Composer } from './components/Composer';
import { SuggestedQuestions } from './components/SuggestedQuestions';
import { useChat } from './hooks/use-chat';
import { DEFAULT_PERSONA, PERSONAS } from './lib/personas';

const MAX_MESSAGE = 2000; // the API rejects longer messages

export function App() {
  const { state, persona, auth, selectPersona, retryAuth, send, stop, busy } = useChat(DEFAULT_PERSONA);
  const [draft, setDraft] = useState('');
  const current = PERSONAS.find((p) => p.id === persona)!;
  const ready = auth.status === 'ready';

  function submit() {
    if (!ready || busy || !draft.trim()) return;
    void send(draft);
    setDraft('');
  }

  return (
    <div className="mx-auto flex min-h-dvh max-w-4xl flex-col px-4">
      <AppHeader persona={persona} auth={auth} onSelect={selectPersona} onRetry={retryAuth} />

      <main className="flex min-h-0 flex-1 flex-col py-6">
        {state.turns.length === 0 ? (
          <SuggestedQuestions persona={current} disabled={!ready} onPick={(q) => void send(q)} />
        ) : (
          <ChatThread turns={state.turns} />
        )}
      </main>

      {/* Sticky, not fixed: the composer sits at the bottom of the viewport while the thread scrolls behind it. */}
      <div className="sticky bottom-0 -mx-4 bg-gradient-to-t from-bg from-60% to-transparent px-4 pt-8">
        <Composer
          value={draft}
          max={MAX_MESSAGE}
          ready={ready}
          busy={busy}
          onChange={setDraft}
          onSubmit={submit}
          onStop={stop}
        />
      </div>
    </div>
  );
}
