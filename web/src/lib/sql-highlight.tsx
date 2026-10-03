import type { ReactNode } from 'react';

const KEYWORDS = new Set(
  `select from where group by order having join inner left right full outer cross on as and or not in is null like between
  exists case when then else end union all distinct top with over partition asc desc count sum avg min max cast convert
  coalesce isnull offset fetch next rows only`.split(/\s+/),
);

// One pass, first match wins at each position: comment, string, bracketed identifier, number, word.
const TOKEN = /(--[^\n]*|\/\*[\s\S]*?\*\/)|('(?:[^']|'')*')|(\[[^\]]*\])|(\b\d+(?:\.\d+)?\b)|([A-Za-z_][A-Za-z0-9_]*)/g;

/**
 * Highlights T-SQL as React nodes. Built from the text with spans, never with innerHTML, so whatever the model wrote
 * is only ever displayed.
 */
export function highlightSql(sql: string): ReactNode[] {
  const out: ReactNode[] = [];
  let last = 0;
  for (const m of sql.matchAll(TOKEN)) {
    const at = m.index;
    if (at > last) out.push(sql.slice(last, at));
    const [text, comment, str, bracket, num, word] = m;
    let cls: string | null = null;
    if (comment) cls = 'text-slate-500 italic';
    else if (str) cls = 'text-emerald-300';
    else if (bracket) cls = 'text-amber-200';
    else if (num) cls = 'text-orange-300';
    else if (word && KEYWORDS.has(word.toLowerCase())) cls = 'text-sky-300 font-semibold';
    out.push(cls ? <span key={at} className={cls}>{text}</span> : text);
    last = at + text.length;
  }
  if (last < sql.length) out.push(sql.slice(last));
  return out;
}
