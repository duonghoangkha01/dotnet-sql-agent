import { AlertIcon } from './icons';

export function QueryErrorCard({ message }: { message: string }) {
  return (
    <div role="alert" className="rounded-xl border border-danger-line bg-danger-soft px-3 py-2.5 text-sm text-danger-fg">
      <p className="flex items-center gap-2 font-semibold">
        <AlertIcon aria-hidden className="size-4 shrink-0" />
        The database rejected the query
      </p>
      <p className="mt-1 pl-6 font-mono text-xs leading-relaxed break-words whitespace-pre-wrap">{message}</p>
    </div>
  );
}
