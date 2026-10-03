import type { Call } from '../hooks/chat-reducer';
import { formatCell, formatCount } from '../lib/format';
import { ClockIcon, TableIcon } from './icons';

type Result = NonNullable<Call['result']>;

/** Rows come from the server in full (at most 500); the table scrolls inside a fixed height with a sticky header. */
export function ResultTable({ result }: { result: Result }) {
  const { columns, rows, rowCount, truncated, elapsedMs } = result;
  const numeric = columns.map((_, i) => rows.some((r) => typeof r[i] === 'number') && rows.every((r) => r[i] === null || typeof r[i] === 'number'));

  return (
    <div className="overflow-hidden rounded-xl border border-line bg-surface shadow-card">
      <div className="flex items-center justify-between gap-3 border-b border-line bg-surface-2/70 px-3 py-2 text-xs text-subtle">
        <span className="flex items-center gap-1.5 font-medium text-muted">
          <TableIcon className="size-3.5" />
          Result
        </span>
        <span className="flex items-center gap-2">
          {truncated && (
            <span className="rounded-md border border-accent-line bg-accent-soft px-1.5 py-0.5 font-medium text-accent-fg">
              {formatCount(rowCount)}+ rows (truncated)
            </span>
          )}
          {!truncated && (
            <span className="tabular-nums">
              {formatCount(rowCount)} {rowCount === 1 ? 'row' : 'rows'}
            </span>
          )}
          <span className="flex items-center gap-1 tabular-nums">
            <ClockIcon className="size-3.5" />
            {formatCount(elapsedMs)} ms
          </span>
        </span>
      </div>

      <div className="max-h-80 overflow-auto">
        <table className="w-full border-collapse text-sm">
          <thead className="sticky top-0 z-10 bg-surface-3/95 text-left text-xs text-muted backdrop-blur">
            <tr>
              {columns.map((c, i) => (
                <th
                  key={c.name + i}
                  title={c.type}
                  className={`border-b border-line px-3 py-2 font-semibold whitespace-nowrap ${numeric[i] ? 'text-right' : ''}`}
                >
                  {c.name}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {rows.length === 0 && (
              <tr>
                <td colSpan={Math.max(columns.length, 1)} className="px-3 py-6 text-center text-subtle">
                  No rows.
                </td>
              </tr>
            )}
            {rows.map((row, r) => (
              <tr key={r} className="border-t border-line/60 transition-colors even:bg-surface-2/40 hover:bg-primary-soft/50">
                {columns.map((c, i) => (
                  <td key={c.name + i} className={`px-3 py-1.5 whitespace-nowrap ${numeric[i] ? 'text-right font-mono text-[0.8125rem] tabular-nums' : ''}`}>
                    {formatCell(row[i] ?? null, c.name)}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
