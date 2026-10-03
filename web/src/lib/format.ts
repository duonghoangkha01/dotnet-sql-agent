import type { CellValue } from '../api/types';

const numbers = new Intl.NumberFormat('en-US', { maximumFractionDigits: 2 });

/** Identifier columns (CustomerID, ProductNumber...) read better without thousands separators. */
const looksLikeIdentifier = (column: string) => /(^|_)id$|[a-z]ID$|number$/i.test(column);

export function formatCell(value: CellValue, column: string): string {
  if (value === null) return '';
  if (typeof value === 'number') return looksLikeIdentifier(column) ? String(value) : numbers.format(value);
  return String(value);
}

export const formatCount = (n: number) => numbers.format(n);
