export function QueryErrorCard({ message }: { message: string }) {
  return (
    <div role="alert" className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-900">
      <p className="font-semibold">The database rejected the query</p>
      <p className="mt-0.5 break-words whitespace-pre-wrap">{message}</p>
    </div>
  );
}
