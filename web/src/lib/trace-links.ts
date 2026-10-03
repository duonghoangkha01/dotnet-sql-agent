/** The Aspire Dashboard is published on 127.0.0.1 only (docker-compose.yml). The detail route was checked against 13.5.2. */
const DASHBOARD = 'http://127.0.0.1:18888';

export const traceUrl = (traceId: string) => `${DASHBOARD}/traces/detail/${encodeURIComponent(traceId)}`;

/** Fallback: spans are exported in batches, so the trace may not exist yet when its link is first clicked. */
export const traceListUrl = `${DASHBOARD}/traces`;
