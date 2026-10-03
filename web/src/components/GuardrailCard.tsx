import type { Violation } from '../api/types';
import { ShieldIcon } from './icons';

/** The guardrail refused the query before it reached the database. Shown as a result, not as a failure. */
export function GuardrailCard({ violations }: { violations: Violation[] }) {
  return (
    <div className="rounded-xl border border-accent-line bg-accent-soft px-3 py-2.5 text-sm text-accent-fg">
      <p className="flex items-center gap-2 font-semibold">
        <ShieldIcon aria-hidden className="size-4 shrink-0" />
        Blocked by the SQL guardrail. This query never reached the database.
      </p>
      <ul className="mt-2 space-y-1.5">
        {violations.map((v, i) => (
          <li key={v.code + i} className="flex gap-2">
            <code className="shrink-0 rounded border border-accent-line bg-accent-soft px-1.5 font-mono text-xs leading-5">{v.code}</code>
            <span className="break-words">{v.message}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}
