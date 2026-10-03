import type { Persona } from '../lib/personas';

export function SuggestedQuestions({ persona, disabled, onPick }: { persona: Persona; disabled: boolean; onPick: (q: string) => void }) {
  return (
    <div className="space-y-3 py-8 text-center">
      <p className="text-slate-600">
        Ask about the AdventureWorks data as <strong>{persona.label}</strong> <span className="text-slate-500">({persona.hint})</span>.
      </p>
      <div className="flex flex-wrap justify-center gap-2">
        {persona.questions.map((q) => (
          <button
            key={q}
            type="button"
            disabled={disabled}
            onClick={() => onPick(q)}
            className="rounded-full border border-slate-300 bg-white px-3 py-1.5 text-left text-sm text-slate-700 hover:border-sky-400 hover:text-sky-800 disabled:opacity-50"
          >
            {q}
          </button>
        ))}
      </div>
    </div>
  );
}
