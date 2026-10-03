import type { Call, Turn } from '../hooks/chat-reducer';
import { GuardrailCard } from './GuardrailCard';
import { QueryErrorCard } from './QueryErrorCard';
import { ResultTable } from './ResultTable';
import { SqlBlock } from './SqlBlock';
import { TurnFooter } from './TurnFooter';

function activityLabel(call: Call): string {
  if (call.tool === 'list_tables') return 'Looking up the tables you can use';
  if (call.tool === 'describe_tables') return 'Reading table details';
  return call.purpose ? `Query: ${call.purpose}` : 'Running a query';
}

function ToolActivity({ call, turnRunning }: { call: Call; turnRunning: boolean }) {
  const spinning = turnRunning && call.outcome === 'running' && call.tool === 'run_sql';
  return (
    <p className="flex items-center gap-2 text-sm text-slate-600">
      <span aria-hidden className={`inline-block h-2 w-2 rounded-full ${spinning ? 'animate-pulse bg-sky-500' : 'bg-slate-300'}`} />
      {activityLabel(call)}
    </p>
  );
}

function CallView({ call, turnRunning }: { call: Call; turnRunning: boolean }) {
  return (
    <div className="space-y-2">
      <ToolActivity call={call} turnRunning={turnRunning} />
      {call.tool === 'run_sql' && call.sql && <SqlBlock sql={call.sql} />}
      {call.outcome === 'result' && call.result && <ResultTable result={call.result} />}
      {call.outcome === 'query_error' && <QueryErrorCard message={call.errorMessage ?? 'Unknown error.'} />}
      {call.outcome === 'blocked' && <GuardrailCard violations={call.violations ?? []} />}
    </div>
  );
}

/**
 * One answer. The model's text is shown as plain text: React escapes it, `whitespace-pre-wrap` keeps its line breaks,
 * and nothing here parses markdown, so an image or link in the text is only ever literal characters.
 */
export function AssistantTurn({ turn }: { turn: Turn }) {
  const running = turn.status === 'streaming';
  return (
    <div className="space-y-3">
      {turn.calls.map((call) => (
        <CallView key={call.callId} call={call} turnRunning={running} />
      ))}

      {turn.texts.map((text, i) => (
        <p
          key={i}
          className={
            i === 0
              ? 'rounded-lg bg-white px-4 py-3 text-slate-900 shadow-sm whitespace-pre-wrap break-words'
              : 'rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-900 whitespace-pre-wrap break-words'
          }
        >
          {text}
        </p>
      ))}

      {running && turn.texts.length === 0 && <p className="animate-pulse text-sm text-slate-500">Working…</p>}
      {turn.status === 'error' && <QueryErrorCard message={turn.errorMessage ?? 'Something went wrong.'} />}
      {turn.status === 'aborted' && <p className="text-sm text-slate-600">Stopped.</p>}
      {turn.notSaved && (
        <p className="text-xs text-slate-500">Not saved: this turn is not part of the conversation, so your next question continues without it.</p>
      )}

      <TurnFooter turn={turn} />
    </div>
  );
}
