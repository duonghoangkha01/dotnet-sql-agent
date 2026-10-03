import type { PersonaId } from '../api/types';
import type { AuthState } from '../hooks/use-chat';
import { PERSONAS } from '../lib/personas';

interface Props {
  persona: PersonaId;
  auth: AuthState;
  onSelect: (id: PersonaId) => void;
  onRetry: () => void;
}

/** Switching persona starts a new conversation: the token (and so the role and territory) changes with it. */
export function PersonaSwitcher({ persona, auth, onSelect, onRetry }: Props) {
  return (
    <div className="flex flex-col gap-1">
      <div role="radiogroup" aria-label="Persona" className="inline-flex rounded-lg bg-slate-200 p-1">
        {PERSONAS.map((p) => (
          <button
            key={p.id}
            type="button"
            role="radio"
            aria-checked={p.id === persona}
            title={p.hint}
            onClick={() => onSelect(p.id)}
            className={`rounded-md px-3 py-1.5 text-sm font-medium transition ${
              p.id === persona ? 'bg-white text-slate-900 shadow-sm' : 'text-slate-600 hover:text-slate-900'
            }`}
          >
            {p.label}
          </button>
        ))}
      </div>
      {auth.status === 'loading' && <p className="text-xs text-slate-500">Signing in…</p>}
      {auth.status === 'error' && (
        <p role="alert" className="text-xs text-red-700">
          Could not sign in: {auth.message}{' '}
          <button type="button" onClick={onRetry} className="underline">
            Retry
          </button>
        </p>
      )}
    </div>
  );
}
