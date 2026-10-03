import type { Violation } from '../api/types';

/** The guardrail refused the query before it reached the database. Shown as a result, not as a failure. */
export function GuardrailCard({ violations }: { violations: Violation[] }) {
  return (
    <div className="rounded-lg border border-amber-300 bg-amber-50 px-3 py-2 text-sm text-amber-950">
      <p className="font-semibold">Blocked by the SQL guardrail. This query never reached the database.</p>
      <ul className="mt-1 space-y-1">
        {violations.map((v, i) => (
          <li key={v.code + i} className="flex gap-2">
            <code className="shrink-0 rounded bg-amber-200/70 px-1.5 text-xs leading-5">{v.code}</code>
            <span className="break-words">{v.message}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}
