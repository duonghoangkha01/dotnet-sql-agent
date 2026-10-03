import { describe, expect, it } from 'vitest';
import { formatCell } from './format';

describe('formatCell', () => {
  it('groups thousands in amounts and counts', () => {
    expect(formatCell(1234567.891, 'Revenue')).toBe('1,234,567.89');
    expect(formatCell(2053, 'OrderCount')).toBe('2,053');
  });

  it.each(['OrderYear', 'Year', 'fiscal_year'])('leaves the year column %s ungrouped', (column) => {
    expect(formatCell(2013, column)).toBe('2013');
  });

  it.each(['CustomerID', 'customer_id', 'ProductNumber'])('leaves the identifier column %s ungrouped', (column) => {
    expect(formatCell(10234, column)).toBe('10234');
  });

  it('renders null as empty and text as is', () => {
    expect(formatCell(null, 'Name')).toBe('');
    expect(formatCell('Bikes', 'Category')).toBe('Bikes');
  });
});
