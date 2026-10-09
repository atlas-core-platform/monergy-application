import { useCallback, useContext, useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { AccessApi, AccessApiError, establishSession } from './accessApi';
import type { TenantSession } from './accessApi';
import { Context, sessionMemory } from './workspaceSession';
import type { WorkspaceSessionState } from './workspaceSession';

type Snapshot = Pick<WorkspaceSessionState, 'status' | 'session' | 'api' | 'message' | 'recovery'>;
const empty: Snapshot = { status: 'public', session: null, api: null, message: '', recovery: null };
const identifier = (value: unknown, maximum: number): value is string =>
  typeof value === 'string' &&
  value.length <= maximum &&
  /^[A-Za-z0-9][A-Za-z0-9_.:-]*$/.test(value);
function validSession(value: unknown, tenant: string): value is TenantSession {
  if (!value || typeof value !== 'object') return false;
  const current = value as Partial<TenantSession>;
  return (
    current.tenantId === tenant &&
    identifier(current.actorId, 128) &&
    identifier(current.authenticationContextId, 160) &&
    typeof current.subjectVersion === 'number' &&
    Number.isSafeInteger(current.subjectVersion) &&
    current.subjectVersion > 0 &&
    typeof current.expiresAt === 'string' &&
    Number.isFinite(Date.parse(current.expiresAt)) &&
    Date.parse(current.expiresAt) > Date.now()
  );
}
async function verify(current: TenantSession, signal: AbortSignal) {
  const context = await new AccessApi(current).request<{
    tenantId: string;
    actorId: string;
    authority: string;
    policyVersion: number;
  } | null>('administration/context', 'GET', undefined, signal);
  if (
    !validSession(current, current.tenantId) ||
    context?.tenantId !== current.tenantId ||
    context.actorId !== current.actorId ||
    context.authority !== 'TenantAdmin' ||
    !Number.isSafeInteger(context.policyVersion) ||
    context.policyVersion < 1
  )
    throw new AccessApiError(503, 'OWNER_CONTEXT_INVALID');
}

// Also used by isolated component hosts, without creating nested session owners.
export function WorkspaceSessionProvider({ children }: { children: ReactNode }) {
  const parent = useContext(Context);
  return parent ? children : <SessionOwner>{children}</SessionOwner>;
}
function SessionOwner({ children }: { children: ReactNode }) {
  const [state, setState] = useState<Snapshot>(empty);
  const epoch = useRef(0);
  const request = useRef<AbortController | null>(null);
  const candidate = useRef<TenantSession | null>(null);
  const recovery = useRef<Snapshot['recovery']>(null);
  const mounted = useRef(true);
  const stop = useCallback(() => {
    epoch.current += 1;
    request.current?.abort();
    request.current = null;
    sessionMemory.current = null;
    return epoch.current;
  }, []);
  const leave = useCallback(
    (message: string, disconnected = false) => {
      stop();
      if (!disconnected) {
        candidate.current = null;
        recovery.current = null;
      }
      setState({ ...empty, status: disconnected ? 'disconnected' : 'public', message });
    },
    [stop],
  );
  const publish = useCallback(
    (current: TenantSession, id: number) => {
      if (!mounted.current || id !== epoch.current) return;
      const controller = new AbortController();
      request.current = controller;
      candidate.current = current;
      const api = new AccessApi(current, {
        signal: controller.signal,
        onFailure: (failure) => {
          if (id !== epoch.current) return;
          leave(
            failure.status === 503
              ? 'Workspace connection interrupted. Reconnect to verify your session before continuing.'
              : failure.message,
            failure.status === 503,
          );
        },
        onOperation: (operationId) => {
          if (id !== epoch.current) return;
          recovery.current = operationId
            ? { tenantId: current.tenantId, actorId: current.actorId, operationId }
            : null;
          if (!operationId) setState((previous) => ({ ...previous, recovery: null }));
        },
      });
      if (
        recovery.current &&
        (recovery.current.tenantId !== current.tenantId ||
          recovery.current.actorId !== current.actorId)
      )
        recovery.current = null;
      sessionMemory.current = current;
      setState({
        status: 'authorized',
        session: current,
        api,
        message: '',
        recovery: recovery.current,
      });
    },
    [leave],
  );
  const connect = useCallback(
    async (tenant: string, key: string) => {
      const id = stop();
      const controller = new AbortController();
      request.current = controller;
      candidate.current = null;
      setState({ ...empty, status: 'connecting' });
      let issued: TenantSession | null = null;
      try {
        issued = await establishSession(tenant, key, controller.signal);
        if (!validSession(issued, tenant))
          throw new AccessApiError(503, 'SESSION_RESPONSE_INVALID');
        if (id !== epoch.current || !mounted.current) return;
        setState({ ...empty, status: 'verifying' });
        await verify(issued, controller.signal);
        publish(issued, id);
      } catch (failure) {
        if (id !== epoch.current || !mounted.current) return;
        candidate.current = null;
        setState({
          ...empty,
          message:
            failure instanceof AccessApiError && failure.status === 403
              ? 'Tenant administrator access is required.'
              : failure instanceof AccessApiError && failure.status === 401
                ? 'Unable to connect. Check your tenant and local access key, then try again.'
                : 'Workspace verification could not be completed. Check the connection and try again.',
        });
      } finally {
        // Revoke a session issued for a cancelled, denied or superseded attempt.
        if (
          issued &&
          validSession(issued, tenant) &&
          sessionMemory.current?.authenticationContextId !== issued.authenticationContextId
        )
          void new AccessApi(issued).endSession().catch(() => undefined);
      }
    },
    [stop, publish],
  );
  const retry = useCallback(async () => {
    const current = candidate.current;
    if (!current || !validSession(current, current.tenantId)) {
      leave('Your session has ended. Connect again to continue.');
      return;
    }
    const id = stop();
    const controller = new AbortController();
    request.current = controller;
    setState({ ...empty, status: 'verifying' });
    try {
      await verify(current, controller.signal);
      publish(current, id);
    } catch (failure) {
      if (id !== epoch.current || !mounted.current) return;
      const denied = failure instanceof AccessApiError && [401, 403].includes(failure.status);
      leave(
        denied
          ? failure.message
          : 'Workspace connection interrupted. Try reconnecting when the service is available.',
        !denied,
      );
    }
  }, [stop, leave, publish]);
  const signOut = useCallback(async () => {
    const current = candidate.current;
    leave('');
    const id = epoch.current;
    if (!current) return;
    try {
      await new AccessApi(current).endSession();
    } catch {
      if (mounted.current && id === epoch.current)
        setState({
          ...empty,
          message: 'Signed out of this browser. The service could not confirm session revocation.',
        });
    }
  }, [leave]);
  const cancel = useCallback(() => {
    void signOut();
  }, [signOut]);
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      stop();
      candidate.current = null;
      recovery.current = null;
    };
  }, [stop]);
  useEffect(() => {
    if (!state.session) return;
    const expires = Date.parse(state.session.expiresAt);
    let timer: number;
    const checkExpiry = () => {
      const remaining = expires - Date.now();
      if (remaining <= 0) leave('Your session has ended. Connect again to continue.');
      else timer = window.setTimeout(checkExpiry, Math.min(remaining, 2_147_483_647));
    };
    checkExpiry();
    const routeCheck = () => {
      if (Date.now() >= expires) leave('Your session has ended. Connect again to continue.');
    };
    const offline = () => {
      leave(
        'Workspace connection interrupted. Reconnect to verify your session before continuing.',
        true,
      );
    };
    const restore = (event: PageTransitionEvent) => {
      if (event.persisted) void retry();
    };
    const resume = () => {
      if (document.visibilityState === 'visible') void retry();
    };
    window.addEventListener('offline', offline);
    window.addEventListener('popstate', routeCheck);
    window.addEventListener('pageshow', restore);
    document.addEventListener('click', routeCheck, true);
    document.addEventListener('visibilitychange', resume);
    return () => {
      window.clearTimeout(timer);
      window.removeEventListener('offline', offline);
      window.removeEventListener('popstate', routeCheck);
      window.removeEventListener('pageshow', restore);
      document.removeEventListener('click', routeCheck, true);
      document.removeEventListener('visibilitychange', resume);
    };
  }, [state.session, leave, retry]);
  return (
    <Context.Provider value={{ ...state, connect, retry, signOut, cancel }}>
      {children}
    </Context.Provider>
  );
}
