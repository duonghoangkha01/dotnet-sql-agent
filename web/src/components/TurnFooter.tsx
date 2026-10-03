import type { ReactNode } from 'react';
import type { Turn } from '../hooks/chat-reducer';
import { formatCount } from '../lib/format';
import { traceListUrl, traceUrl } from '../lib/trace-links';
import { ClockIcon, ListIcon, TokenIcon, TraceIcon } from './icons';

function Chip({ icon, children }: { icon: ReactNode; children: ReactNode }) {
  return (
    <span className="flex items-center gap-1.5 rounded-md bg-surface-2 px-2 py-1 tabular-nums">
      {icon}
      {children}
    </span>
  );
}

/** rows · ms · tokens · trace. Rows and ms are totals over the turn's queries that ran. */
export function TurnFooter({ turn }: { turn: Turn }) {
  const ran = turn.calls.filter((c) => c.result);
  const rows = ran.reduce((sum, c) => sum + (c.result?.rowCount ?? 0), 0);
  const ms = ran.reduce((sum, c) => sum + (c.result?.elapsedMs ?? 0), 0);

  const link = 'flex items-center gap-1.5 rounded-md px-2 py-1 text-primary-text transition hover:bg-primary-soft';
  return (
    <p className="flex flex-wrap items-center gap-1.5 text-xs text-subtle">
      {ran.length > 0 && (
        <>
          <Chip icon={<ListIcon className="size-3.5" />}>
            {formatCount(rows)} {rows === 1 ? 'row' : 'rows'}
          </Chip>
          <Chip icon={<ClockIcon className="size-3.5" />}>{formatCount(ms)} ms</Chip>
        </>
      )}
      {turn.usage && (
        <Chip icon={<TokenIcon className="size-3.5" />}>
          {formatCount(turn.usage.inputTokens)} in / {formatCount(turn.usage.outputTokens)} out
        </Chip>
      )}
      {turn.traceId && (
        <>
          <a href={traceUrl(turn.traceId)} target="_blank" rel="noopener noreferrer" className={link}>
            <TraceIcon className="size-3.5" />
            View trace
          </a>
          {/* Spans are exported in batches, so the trace can take a few seconds to appear. */}
          <a
            href={traceListUrl}
            target="_blank"
            rel="noopener noreferrer"
            className={link}
            title="If the trace is not there yet, wait a few seconds"
          >
            all traces
          </a>
        </>
      )}
    </p>
  );
}
