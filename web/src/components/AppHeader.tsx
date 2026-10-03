import type { PersonaId } from '../api/types';
import type { AuthState } from '../hooks/use-chat';
import { useTheme } from '../hooks/use-theme';
import { DatabaseIcon, MoonIcon, SunIcon } from './icons';
import { PersonaSwitcher } from './PersonaSwitcher';

interface Props {
  persona: PersonaId;
  auth: AuthState;
  onSelect: (id: PersonaId) => void;
  onRetry: () => void;
}

function ThemeToggle() {
  const { theme, toggle } = useTheme();
  const next = theme === 'dark' ? 'light' : 'dark';
  return (
    <button
      type="button"
      onClick={toggle}
      aria-label={`Switch to ${next} theme`}
      title={`Switch to ${next} theme`}
      className="flex size-9 items-center justify-center rounded-lg border border-line bg-surface text-muted transition hover:border-line-strong hover:text-fg"
    >
      {theme === 'dark' ? <SunIcon className="size-4.5" /> : <MoonIcon className="size-4.5" />}
    </button>
  );
}

/** Sticky, translucent: the thread scrolls under it, and the persona stays visible because it decides the answers. */
export function AppHeader({ persona, auth, onSelect, onRetry }: Props) {
  return (
    <header className="sticky top-0 z-20 -mx-4 border-b border-line/80 bg-bg/80 px-4 backdrop-blur-xl">
      <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-3 py-3">
        <div className="flex min-w-0 items-center gap-3">
          <span className="flex size-10 shrink-0 items-center justify-center rounded-xl bg-primary text-primary-fg shadow-card">
            <DatabaseIcon className="size-5.5" />
          </span>
          <div className="min-w-0">
            <h1 className="text-base leading-tight font-semibold tracking-tight">SQL Agent</h1>
            <p className="truncate text-xs text-subtle">The SQL, the rows and every refusal are shown.</p>
          </div>
        </div>

        <div className="flex items-center gap-2">
          <PersonaSwitcher persona={persona} auth={auth} onSelect={onSelect} onRetry={onRetry} />
          <ThemeToggle />
        </div>
      </div>
    </header>
  );
}
