import type { PersonaId } from '../api/types';
import type { AuthState } from '../hooks/use-chat';
import { PERSONAS } from '../lib/personas';

interface Props {
  persona: PersonaId;
  auth: AuthState;
  onSelect: (id: PersonaId) => void;
  onRetry: () => void;
}

/** Signed in, signing in or failed. Shape and text carry the state too, never the color alone. */
function AuthDot({ auth }: { auth: AuthState }) {
  const tone =
    auth.status === 'ready' ? 'bg-ok' : auth.status === 'loading' ? 'animate-blink bg-accent-fg' : 'bg-danger-fg';
  const label =
    auth.status === 'ready' ? 'Signed in' : auth.status === 'loading' ? 'Signing in…' : 'Sign-in failed';
  return (
    <span className="flex items-center gap-1.5 text-xs text-subtle" title={label}>
      <span aria-hidden className={`size-2 shrink-0 rounded-full ${tone}`} />
      <span className="sr-only sm:not-sr-only">{label}</span>
    </span>
  );
}

/** Switching persona starts a new conversation: the token (and so the role and territory) changes with it. */
export function PersonaSwitcher({ persona, auth, onSelect, onRetry }: Props) {
  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
      <div
        role="radiogroup"
        aria-label="Persona"
        className="inline-flex rounded-xl border border-line bg-surface-2 p-1"
      >
        {PERSONAS.map((p) => {
          const active = p.id === persona;
          return (
            <button
              key={p.id}
              type="button"
              role="radio"
              aria-checked={active}
              title={p.hint}
              onClick={() => onSelect(p.id)}
              className={`rounded-lg px-3 py-1.5 text-sm font-medium transition ${
                active
                  ? 'bg-surface text-fg shadow-card'
                  : 'text-muted hover:bg-surface/60 hover:text-fg'
              }`}
            >
              {p.label}
            </button>
          );
        })}
      </div>
      <AuthDot auth={auth} />
      {auth.status === 'error' && (
        <p role="alert" className="basis-full text-xs text-danger-fg">
          Could not sign in: {auth.message}{' '}
          <button type="button" onClick={onRetry} className="font-medium underline underline-offset-2">
            Retry
          </button>
        </p>
      )}
    </div>
  );
}
