import { FALLBACK_ANSWER_PREFIX, type Call, type Turn } from '../hooks/chat-reducer';
import { GuardrailCard } from './GuardrailCard';
import { AlertIcon, DatabaseIcon, ListIcon, SparkIcon, StopIcon, TableIcon } from './icons';
import { QueryErrorCard } from './QueryErrorCard';
import { ResultTable } from './ResultTable';
import { SqlBlock } from './SqlBlock';
import { TurnFooter } from './TurnFooter';

function activityLabel(call: Call): string {
  if (call.tool === 'list_tables') return 'Looking up the tables you can use';
  if (call.tool === 'describe_tables') return 'Reading table details';
  return call.purpose ? `Query: ${call.purpose}` : 'Running a query';
}

function StepIcon({ call, spinning }: { call: Call; spinning: boolean }) {
  const Glyph = call.tool === 'list_tables' ? ListIcon : call.tool === 'describe_tables' ? TableIcon : DatabaseIcon;
  const tone =
    call.outcome === 'blocked'
      ? 'border-accent-line bg-accent-soft text-accent-fg'
      : call.outcome === 'query_error'
        ? 'border-danger-line bg-danger-soft text-danger-fg'
        : spinning
          ? 'border-primary/40 bg-primary-soft text-primary-text'
          : 'border-line bg-surface-2 text-muted';
  return (
    <span className={`flex size-7 shrink-0 items-center justify-center rounded-lg border ${tone} ${spinning ? 'animate-blink' : ''}`}>
      <Glyph className="size-4" />
    </span>
  );
}

/** One step of the turn: what the agent did, the SQL it used, and what came back. */
function CallView({ call, turnRunning }: { call: Call; turnRunning: boolean }) {
  const spinning = turnRunning && call.outcome === 'running' && call.tool === 'run_sql';
  return (
    <div className="relative pl-9 sm:pl-10">
      {/* The rail that ties the steps of one turn together. */}
      <span aria-hidden className="absolute top-8 bottom-[-0.75rem] left-[0.84rem] w-px bg-line" />
      <div className="absolute top-0 left-0">
        <StepIcon call={call} spinning={spinning} />
      </div>
      <div className="space-y-2">
        <p className="flex min-h-7 items-center text-sm text-muted">{activityLabel(call)}</p>
        {call.tool === 'run_sql' && call.sql && <SqlBlock sql={call.sql} />}
        {call.outcome === 'result' && call.result && <ResultTable result={call.result} />}
        {call.outcome === 'query_error' && <QueryErrorCard message={call.errorMessage ?? 'Unknown error.'} />}
        {call.outcome === 'blocked' && <GuardrailCard violations={call.violations ?? []} />}
      </div>
    </div>
  );
}

function Working() {
  return (
    <p className="flex items-center gap-2 pl-9 text-sm text-subtle sm:pl-10">
      <span aria-hidden className="flex gap-1">
        <span className="animate-blink size-1.5 rounded-full bg-primary-text [animation-delay:0ms]" />
        <span className="animate-blink size-1.5 rounded-full bg-primary-text [animation-delay:160ms]" />
        <span className="animate-blink size-1.5 rounded-full bg-primary-text [animation-delay:320ms]" />
      </span>
      Working…
    </p>
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
      {turn.calls.length > 0 && (
        <div className="space-y-3">
          {turn.calls.map((call) => (
            <CallView key={call.callId} call={call} turnRunning={running} />
          ))}
        </div>
      )}

      {turn.texts.map((text, i) => {
        // The first text is the answer; the server's own fallback wording is shown as a caution, not as an answer.
        if (i === 0) {
          const unverified = text.startsWith(FALLBACK_ANSWER_PREFIX);
          return (
            <div
              key={i}
              className={`rounded-2xl border px-4 py-3.5 shadow-card ${
                unverified ? 'border-accent-line bg-accent-soft' : 'border-line bg-surface'
              }`}
            >
              <p className={`mb-1.5 flex items-center gap-1.5 text-xs font-semibold tracking-wide uppercase ${unverified ? 'text-accent-fg' : 'text-subtle'}`}>
                {unverified ? <AlertIcon className="size-3.5" /> : <SparkIcon className="size-3.5" />}
                {unverified ? 'Not answered' : 'Answer'}
              </p>
              <p className={`text-[0.9375rem] leading-7 break-words whitespace-pre-wrap ${unverified ? 'text-accent-fg' : 'text-fg'}`}>{text}</p>
            </div>
          );
        }
        return (
          <p
            key={i}
            className="flex gap-2 rounded-xl border border-accent-line bg-accent-soft px-3 py-2.5 text-sm text-accent-fg break-words whitespace-pre-wrap"
          >
            <AlertIcon aria-hidden className="mt-0.5 size-4 shrink-0" />
            {text}
          </p>
        );
      })}

      {running && turn.texts.length === 0 && <Working />}
      {turn.status === 'error' && <QueryErrorCard message={turn.errorMessage ?? 'Something went wrong.'} />}
      {turn.status === 'aborted' && (
        <p className="flex items-center gap-2 text-sm text-muted">
          <StopIcon aria-hidden className="size-3.5 text-subtle" />
          Stopped.
        </p>
      )}
      {turn.notSaved && (
        <p className="text-xs text-subtle">
          Not saved: this turn is not part of the conversation, so your next question continues without it.
        </p>
      )}

      <TurnFooter turn={turn} />
    </div>
  );
}
