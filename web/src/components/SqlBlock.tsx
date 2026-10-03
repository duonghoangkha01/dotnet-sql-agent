import { useState } from 'react';
import { highlightSql } from '../lib/sql-highlight';
import { CheckIcon, ChevronIcon, CopyIcon } from './icons';

/** Collapsible, highlighted SQL with a copy button. Open by default: showing the SQL is the point. */
export function SqlBlock({ sql }: { sql: string }) {
  const [copied, setCopied] = useState(false);

  async function copy() {
    try {
      await navigator.clipboard.writeText(sql);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      // Clipboard not available (permissions, insecure context): the text can still be selected by hand.
    }
  }

  return (
    <details open className="group overflow-hidden rounded-xl border border-line bg-code-bg shadow-card">
      <summary className="flex list-none items-center justify-between gap-2 px-3 py-2 text-xs text-code-chrome select-none [&::-webkit-details-marker]:hidden">
        <span className="flex items-center gap-1.5 font-mono tracking-wide">
          <ChevronIcon className="size-3.5 transition-transform duration-200 group-open:rotate-0 rotate-[-90deg]" />
          SQL
        </span>
        <button
          type="button"
          onClick={(e) => {
            e.preventDefault(); // a click on the button must not toggle the <details>
            void copy();
          }}
          aria-label={copied ? 'SQL copied' : 'Copy SQL'}
          className="flex items-center gap-1.5 rounded-md px-2 py-1 text-code-chrome transition hover:bg-white/10 hover:text-code-fg"
        >
          {copied ? <CheckIcon className="size-3.5 text-[color:var(--ok)]" /> : <CopyIcon className="size-3.5" />}
          {copied ? 'Copied' : 'Copy'}
        </button>
      </summary>
      <pre className="overflow-x-auto border-t border-white/5 px-3.5 py-3 font-mono text-xs leading-relaxed text-code-fg whitespace-pre-wrap">
        {highlightSql(sql)}
      </pre>
    </details>
  );
}
