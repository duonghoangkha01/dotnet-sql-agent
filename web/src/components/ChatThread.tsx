import { useEffect, useRef } from 'react';
import type { Turn } from '../hooks/chat-reducer';
import { AssistantTurn } from './AssistantTurn';

/** Question bubbles and answers, scrolled to the newest. Re-scrolls as an answer grows (rows and text arrive in pieces). */
export function ChatThread({ turns }: { turns: Turn[] }) {
  const end = useRef<HTMLDivElement>(null);
  const last = turns.at(-1);
  const growth = last ? `${last.id}:${last.calls.length}:${last.texts.length}:${last.status}` : '';

  useEffect(() => {
    end.current?.scrollIntoView?.({ block: 'end' });
  }, [turns.length, growth]);

  return (
    <div role="log" aria-live="polite" className="space-y-6">
      {turns.map((turn) => (
        <section key={turn.id} className="space-y-3">
          <p className="ml-auto w-fit max-w-[85%] rounded-2xl bg-sky-600 px-4 py-2 text-white whitespace-pre-wrap break-words">{turn.question}</p>
          <AssistantTurn turn={turn} />
        </section>
      ))}
      <div ref={end} />
    </div>
  );
}
