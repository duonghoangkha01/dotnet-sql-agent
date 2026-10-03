import type { CellValue } from '../api/types';

const numbers = new Intl.NumberFormat('en-US', { maximumFractionDigits: 2 });

/** Identifier and year columns (CustomerID, ProductNumber, OrderYear...) read wrong with thousands separators: 2,013. */
const isPlainNumberColumn = (column: string) => /(^|_)id$|[a-z]ID$|number$|year$/i.test(column);

export function formatCell(value: CellValue, column: string): string {
  if (value === null) return '';
  if (typeof value === 'number') return isPlainNumberColumn(column) ? String(value) : numbers.format(value);
  return String(value);
}

export const formatCount = (n: number) => numbers.format(n);
