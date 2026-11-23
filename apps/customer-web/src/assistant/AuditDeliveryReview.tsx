import { Alert, Button } from 'antd';
import { useRef, useState } from 'react';
import type { AuditDeliveryEvent, AuditDeliveryPage } from './assistantApi';

export default function AuditDeliveryReview({ load, retry }: {
  load: (cursor: string | null) => Promise<AuditDeliveryPage | undefined>;
  retry: (eventId: string, operationId: string) => Promise<AuditDeliveryEvent | undefined>;
}) {
  const [page, setPage] = useState<AuditDeliveryPage | null>(null);
  const [busy, setBusy] = useState(false);
  const [uncertain, setUncertain] = useState(false);
  const operations = useRef(new Map<string, string>());
  const read = async (cursor: string | null) => {
    setBusy(true);
    try { const result = await load(cursor); if (result) setPage(result); }
    finally { setBusy(false); }
  };
  return <section aria-label="Review history recovery">
    <h3>Review history delivery</h3>
    <p>Check recorded review actions, including list access. Retrying delivery does not repeat a saved change.</p>
    <Button loading={busy} onClick={() => { void read(null); }}>Check all review history</Button>
    {uncertain && <Alert type="warning" title="We couldn’t confirm the delivery retry. Retry the same action to check." />}
    {page?.events.length === 0 && <p>No review history is waiting for delivery.</p>}
    {page?.events.map(event => <article key={event.eventId}>
      <p>{event.resourceId === null ? 'Review list access' : 'Question review'} · {event.state} · {event.createdAt}</p>
      {event.state === 'Paused' && event.recoveries < 3 && <Button disabled={busy} onClick={() => {
        const operation = operations.current.get(event.eventId) ?? crypto.randomUUID();
        operations.current.set(event.eventId, operation); setBusy(true);
        void retry(event.eventId, operation).then(result => {
          if (!result) { setUncertain(true); return; }
          operations.current.delete(event.eventId); setUncertain(false);
          setPage(current => current ? { ...current, events: current.events.map(item => item.eventId === result.eventId ? result : item) } : current);
        }).finally(() => { setBusy(false); });
      }}>Retry history delivery</Button>}
    </article>)}
    {page?.nextCursor && <Button disabled={busy} onClick={() => { void read(page.nextCursor); }}>Next history</Button>}
  </section>;
}
