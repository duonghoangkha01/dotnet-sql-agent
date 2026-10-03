import { useEffect, useRef } from 'react';
import type { Turn } from '../hooks/chat-reducer';
import { AssistantTurn } from './AssistantTurn';

/** Question bubbles and answers, scrolled to the newest. Re-scrolls as an answer grows (rows and text arrive in pieces). */
export function ChatThread({ turns }: { turns: Turn[] }) {
  const end = useRef<HTMLDivElement>(null);
  const last = turns.at(-1);
  const growth = last ? `${last.id}:${last.calls.length}:${last.texts.length}:${last.status}` : '';

  useEffect(() => {
    end.current?.scrollIntoView?.({ block: 'end', behavior: 'smooth' });
  }, [turns.length, growth]);

  return (
    <div role="log" aria-live="polite" className="space-y-8">
      {turns.map((turn) => (
        <section key={turn.id} className="animate-rise space-y-4">
          <div className="flex justify-end">
            <p className="w-fit max-w-[85%] rounded-2xl rounded-br-md bg-primary px-4 py-2.5 text-[0.9375rem] leading-6 text-primary-fg shadow-card break-words whitespace-pre-wrap">
              {turn.question}
            </p>
          </div>
          <AssistantTurn turn={turn} />
        </section>
      ))}
      {/* The sentinel keeps a gap for the sticky composer, so the newest row is never hidden behind it. */}
      <div ref={end} className="scroll-mb-36" />
    </div>
  );
}
