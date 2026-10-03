import type { Persona } from '../lib/personas';
import { ArrowIcon, DatabaseIcon, ShieldIcon } from './icons';

interface Props {
  persona: Persona;
  disabled: boolean;
  onPick: (q: string) => void;
}

/**
 * The empty state. The last question of every persona is the one that tries to get past the guardrails
 * (see the `questions` contract in lib/personas.ts), so it is labelled rather than hidden: a refusal shown
 * in full is the most interesting thing this demo does.
 */
export function SuggestedQuestions({ persona, disabled, onPick }: Props) {
  const probe = persona.questions.length - 1;

  return (
    <div className="flex flex-1 flex-col justify-center py-6">
      <div className="animate-fade mb-7 text-center">
        <span className="mx-auto mb-4 flex size-12 items-center justify-center rounded-2xl border border-line bg-surface text-primary-text shadow-card">
          <DatabaseIcon className="size-6" />
        </span>
        <h2 className="text-xl font-semibold tracking-tight text-balance">Ask the AdventureWorks database</h2>
        <p className="mx-auto mt-2 max-w-md text-sm leading-6 text-muted">
          You are asking as <span className="font-medium text-fg">{persona.label}</span> — {persona.hint}. The agent writes
          the T-SQL, runs it as that role and shows you both.
        </p>
      </div>

      <ul className="grid gap-2.5 sm:grid-cols-2">
        {persona.questions.map((q, i) => {
          const isProbe = i === probe;
          return (
            <li key={q}>
              <button
                type="button"
                disabled={disabled}
                onClick={() => onPick(q)}
                className={`group flex h-full w-full items-start gap-2.5 rounded-xl border px-3.5 py-3 text-left text-sm leading-6 transition disabled:cursor-not-allowed disabled:opacity-50 ${
                  isProbe
                    ? 'border-accent-line bg-accent-soft text-accent-fg hover:shadow-card'
                    : 'border-line bg-surface text-fg hover:-translate-y-0.5 hover:border-primary/50 hover:shadow-card'
                }`}
              >
                {isProbe ? (
                  <ShieldIcon aria-hidden className="mt-1 size-4 shrink-0" />
                ) : (
                  <ArrowIcon aria-hidden className="mt-1 size-4 shrink-0 text-subtle transition group-hover:translate-x-0.5 group-hover:text-primary-text" />
                )}
                <span className="min-w-0">
                  {q}
                  {isProbe && <span className="mt-1 block text-xs font-medium opacity-80">Tries to get past the guardrails — expect a refusal.</span>}
                </span>
              </button>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
