import { Button } from 'antd';
import { lazy, Suspense, useCallback, useMemo, useState } from 'react';
import type { AssistantSession } from './assistantApi';

const Drawer = lazy(() => import('./AskMonergyDrawer'));
export const assistantEnabled = (import.meta.env as Record<string, unknown>).VITE_MONERGY_ASSISTANT_ENABLED === 'true';

// The authenticated shell keys this boundary by its session and authorization snapshot.
// Closing or changing that boundary destroys all temporary text and pending requests.
export default function AskMonergyAction(props: {
  session: AssistantSession; workspace?: string; onSessionEnded: () => void;
}) {
  const [open, setOpen] = useState(false);
  const close = useCallback(() => { setOpen(false); }, []);
  const session = useMemo(() => ({ audience: props.session.audience, credential: props.session.credential,
    expiresAt: props.session.expiresAt }), [props.session.audience, props.session.credential, props.session.expiresAt]);
  if (!assistantEnabled) return null;
  return <>
    <Button type="text" onClick={() => { setOpen(true); }}>Ask Monergy</Button>
    {open && <Suspense fallback={<span role="status">Opening help…</span>}>
      <Drawer key={`${session.audience}:${session.credential}:${session.expiresAt}:${props.workspace ?? ''}`}
        {...props} session={session} onClose={close} />
    </Suspense>}
  </>;
}
