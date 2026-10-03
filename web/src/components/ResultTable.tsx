import type { Call } from '../hooks/chat-reducer';
import { formatCell, formatCount } from '../lib/format';

type Result = NonNullable<Call['result']>;

/** Rows come from the server in full (at most 500); the table scrolls inside a fixed height with a sticky header. */
export function ResultTable({ result }: { result: Result }) {
  const { columns, rows, rowCount, truncated } = result;
  const numeric = columns.map((_, i) => rows.some((r) => typeof r[i] === 'number') && rows.every((r) => r[i] === null || typeof r[i] === 'number'));

  return (
    <div className="overflow-hidden rounded-lg border border-slate-200 bg-white">
      <div className="max-h-80 overflow-auto">
        <table className="w-full border-collapse text-sm">
          <thead className="sticky top-0 bg-slate-100 text-left text-xs text-slate-600">
            <tr>
              {columns.map((c, i) => (
                <th key={c.name + i} className={`px-3 py-2 font-semibold whitespace-nowrap ${numeric[i] ? 'text-right' : ''}`}>
                  {c.name}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {rows.length === 0 && (
              <tr>
                <td colSpan={Math.max(columns.length, 1)} className="px-3 py-3 text-slate-500">
                  No rows.
                </td>
              </tr>
            )}
            {rows.map((row, r) => (
              <tr key={r} className="border-t border-slate-100 odd:bg-white even:bg-slate-50/60">
                {columns.map((c, i) => (
                  <td key={c.name + i} className={`px-3 py-1.5 whitespace-nowrap ${numeric[i] ? 'text-right tabular-nums' : ''}`}>
                    {formatCell(row[i] ?? null, c.name)}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <div className="flex items-center gap-2 border-t border-slate-100 px-3 py-1.5 text-xs text-slate-500">
        {truncated ? (
          <span className="rounded bg-amber-100 px-1.5 py-0.5 font-medium text-amber-800">{formatCount(rowCount)}+ rows (truncated)</span>
        ) : (
          <span>
            {formatCount(rowCount)} {rowCount === 1 ? 'row' : 'rows'}
          </span>
        )}
      </div>
    </div>
  );
}
