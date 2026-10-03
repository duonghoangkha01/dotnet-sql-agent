import type { Turn } from '../hooks/chat-reducer';
import { formatCount } from '../lib/format';
import { traceListUrl, traceUrl } from '../lib/trace-links';

/** rows · ms · tokens · trace. Rows and ms are totals over the turn's queries that ran. */
export function TurnFooter({ turn }: { turn: Turn }) {
  const ran = turn.calls.filter((c) => c.result);
  const rows = ran.reduce((sum, c) => sum + (c.result?.rowCount ?? 0), 0);
  const ms = ran.reduce((sum, c) => sum + (c.result?.elapsedMs ?? 0), 0);

  const parts: string[] = [];
  if (ran.length > 0) parts.push(`${formatCount(rows)} ${rows === 1 ? 'row' : 'rows'}`, `${formatCount(ms)} ms`);
  if (turn.usage) parts.push(`${formatCount(turn.usage.inputTokens)} in / ${formatCount(turn.usage.outputTokens)} out tokens`);

  const link = 'text-sky-700 underline hover:text-sky-900';
  return (
    <p className="flex flex-wrap items-center gap-x-2 text-xs text-slate-500">
      {parts.join(' · ')}
      {turn.traceId && (
        <>
          {parts.length > 0 && <span aria-hidden>·</span>}
          <a href={traceUrl(turn.traceId)} target="_blank" rel="noopener noreferrer" className={link}>
            View trace
          </a>
          {/* Spans are exported in batches, so the trace can take a few seconds to appear. */}
          <a href={traceListUrl} target="_blank" rel="noopener noreferrer" className={link} title="If the trace is not there yet, wait a few seconds">
            all traces
          </a>
        </>
      )}
    </p>
  );
}
