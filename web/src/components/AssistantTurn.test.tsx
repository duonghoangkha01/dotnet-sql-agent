// @vitest-environment jsdom
import { cleanup, render } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import type { Turn } from '../hooks/chat-reducer';
import { AssistantTurn } from './AssistantTurn';

afterEach(cleanup);

const turn = (patch: Partial<Turn>): Turn => ({ id: 't', question: 'q', status: 'done', calls: [], texts: [], notSaved: false, ...patch });

describe('AssistantTurn output safety', () => {
  it('shows model text as literal text: no image, link or markup is created', () => {
    const evil = 'Done. ![x](http://evil/?d=1) [click](http://evil) <img src="http://evil/p.png"> <script>alert(1)</script>';
    const { container } = render(<AssistantTurn turn={turn({ texts: [evil] })} />);
    expect(container.querySelector('img, script, a')).toBeNull();
    expect(container.textContent).toContain(evil);
  });

  it('shows SQL and error text from the server as literal text too', () => {
    const sql = "SELECT '<img src=x onerror=alert(1)>' AS x";
    const { container } = render(
      <AssistantTurn
        turn={turn({
          calls: [{ callId: 'a', tool: 'run_sql', sql, outcome: 'query_error', errorMessage: '<b>boom</b>' }],
        })}
      />,
    );
    expect(container.querySelector('img, b')).toBeNull();
    expect(container.textContent).toContain(sql);
    expect(container.textContent).toContain('<b>boom</b>');
  });
});

describe('AssistantTurn states', () => {
  it('renders a guardrail block with its violations, not a crash', () => {
    const { container } = render(
      <AssistantTurn
        turn={turn({ calls: [{ callId: 'a', tool: 'run_sql', sql: 'DROP TABLE x', outcome: 'blocked', violations: [{ code: 'NOT_SELECT', message: 'Only SELECT is allowed.' }] }] })}
      />,
    );
    expect(container.textContent).toContain('Blocked by the SQL guardrail');
    expect(container.textContent).toContain('NOT_SELECT');
    expect(container.textContent).toContain('Only SELECT is allowed.');
  });

  it('shows the truncation badge and a sticky-header table for a truncated result', () => {
    const result = { columns: [{ name: 'Total', type: 'decimal' }], rows: [[1234.5], [2]], rowCount: 500, truncated: true, elapsedMs: 9 };
    const { container } = render(<AssistantTurn turn={turn({ calls: [{ callId: 'a', tool: 'run_sql', sql: 'SELECT 1', outcome: 'result', result }] })} />);
    expect(container.textContent).toContain('500+ rows (truncated)');
    expect(container.textContent).toContain('1,234.5');
    expect(container.querySelector('thead.sticky')).not.toBeNull();
  });

  it('links to the trace and falls back to the trace list', () => {
    const { container } = render(<AssistantTurn turn={turn({ traceId: 'abc123' })} />);
    const hrefs = [...container.querySelectorAll('a')].map((a) => a.getAttribute('href'));
    expect(hrefs).toEqual(['http://127.0.0.1:18888/traces/detail/abc123', 'http://127.0.0.1:18888/traces']);
  });

  it('marks an aborted turn as stopped and not saved', () => {
    const { container } = render(<AssistantTurn turn={turn({ status: 'aborted', notSaved: true })} />);
    expect(container.textContent).toContain('Stopped');
    expect(container.textContent).toContain('Not saved');
  });
});
