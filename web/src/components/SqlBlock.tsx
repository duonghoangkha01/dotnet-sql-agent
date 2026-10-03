import { useState } from 'react';
import { highlightSql } from '../lib/sql-highlight';

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
    <details open className="group rounded-lg bg-slate-900 text-slate-100">
      <summary className="flex cursor-pointer select-none items-center justify-between px-3 py-2 text-xs text-slate-400">
        <span>SQL</span>
        <button
          type="button"
          onClick={(e) => {
            e.preventDefault(); // a click on the button must not toggle the <details>
            void copy();
          }}
          className="rounded px-2 py-0.5 text-slate-300 hover:bg-slate-700"
        >
          {copied ? 'Copied' : 'Copy'}
        </button>
      </summary>
      <pre className="overflow-x-auto px-3 pb-3 font-mono text-xs leading-relaxed whitespace-pre-wrap">{highlightSql(sql)}</pre>
    </details>
  );
}
