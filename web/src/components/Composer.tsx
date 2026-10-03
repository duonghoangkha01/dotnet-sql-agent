import { useEffect, useRef, type FormEvent, type KeyboardEvent } from 'react';
import { SendIcon, StopIcon } from './icons';

interface Props {
  value: string;
  max: number;
  ready: boolean;
  busy: boolean;
  onChange: (value: string) => void;
  onSubmit: () => void;
  onStop: () => void;
}

/**
 * The ask box: a textarea that grows with the question (long questions are normal here), Enter to send,
 * Shift+Enter for a new line. While a turn streams, Send becomes Stop in place, so the button never moves.
 */
export function Composer({ value, max, ready, busy, onChange, onSubmit, onStop }: Props) {
  const area = useRef<HTMLTextAreaElement>(null);

  // Grow to the content up to a cap, then scroll inside: measured from scrollHeight after a reset to 'auto'.
  useEffect(() => {
    const el = area.current;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = `${Math.min(el.scrollHeight, 168)}px`;
  }, [value]);

  function submit(e: FormEvent) {
    e.preventDefault();
    onSubmit();
  }

  function keyDown(e: KeyboardEvent<HTMLTextAreaElement>) {
    if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
      e.preventDefault();
      onSubmit();
    }
  }

  const canSend = ready && !busy && value.trim().length > 0;
  const nearLimit = value.length > max - 200;

  return (
    <form onSubmit={submit} className="pb-4">
      <div className="rounded-2xl border border-line bg-surface p-2 shadow-float transition-colors focus-within:border-primary/60">
        <div className="flex items-end gap-2">
          <textarea
            ref={area}
            rows={1}
            value={value}
            onChange={(e) => onChange(e.target.value)}
            onKeyDown={keyDown}
            maxLength={max}
            disabled={!ready}
            placeholder={ready ? 'Ask about the data — "revenue by product category last year"' : 'Waiting for sign-in…'}
            aria-label="Question"
            className="min-h-11 min-w-0 flex-1 resize-none bg-transparent px-3 py-2.5 text-[0.9375rem] leading-6 text-fg outline-none placeholder:text-subtle disabled:opacity-60"
          />
          {busy ? (
            <button
              type="button"
              onClick={onStop}
              aria-label="Stop generating"
              className="flex size-11 shrink-0 items-center justify-center rounded-xl bg-surface-3 text-fg transition hover:bg-line-strong active:scale-95"
            >
              <StopIcon className="size-4" />
            </button>
          ) : (
            <button
              type="submit"
              disabled={!canSend}
              aria-label="Send question"
              className="flex size-11 shrink-0 items-center justify-center rounded-xl bg-primary text-primary-fg transition hover:brightness-110 active:scale-95 disabled:cursor-not-allowed disabled:opacity-40 disabled:active:scale-100"
            >
              <SendIcon className="size-5" />
            </button>
          )}
        </div>
      </div>
      <p className="mt-2 flex items-center justify-between gap-3 px-1 text-xs text-subtle">
        <span>
          <kbd className="rounded border border-line bg-surface-2 px-1.5 py-0.5 font-sans text-[0.6875rem] text-muted">Enter</kbd> to send ·{' '}
          <kbd className="rounded border border-line bg-surface-2 px-1.5 py-0.5 font-sans text-[0.6875rem] text-muted">Shift</kbd>+
          <kbd className="rounded border border-line bg-surface-2 px-1.5 py-0.5 font-sans text-[0.6875rem] text-muted">Enter</kbd> for a new line
        </span>
        {nearLimit && (
          <span className="shrink-0 tabular-nums" aria-live="polite">
            {value.length} / {max}
          </span>
        )}
      </p>
    </form>
  );
}
