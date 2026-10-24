import { useCallback, useContext, useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { AccessApi, AccessApiError, establishSession } from './accessApi';
import type { TenantSession } from './accessApi';
import { registrationRequest } from './registrationApi';
import { confirmCustomerEmailCode } from './customerEmailApi';
import { Context, productContextMemory, sessionMemory } from './workspaceSession';
import type {
  ProductWorkspaceContext,
  WorkspaceAuthorization,
  WorkspaceSessionState,
} from './workspaceSession';

type WorkspaceArea = 'administration' | 'product' | 'kyc-review' | 'advisor';
type Snapshot = Pick<
  WorkspaceSessionState,
  'status' | 'session' | 'api' | 'authorization' | 'message' | 'recovery'
>;
const empty: Snapshot = {
  status: 'public',
  session: null,
  api: null,
  authorization: null,
  message: '',
  recovery: null,
};
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
async function verify(
  current: TenantSession,
  area: WorkspaceArea,
  signal: AbortSignal,
): Promise<WorkspaceAuthorization> {
  if (area === 'kyc-review') {
    const context = await new AccessApi(current).basicKyc<{tenantId:string;actorId:string;authority:string}>('reviewer-context','GET',undefined,signal);
    if(context.tenantId!==current.tenantId || context.actorId!==current.actorId || context.authority!=='KycReviewer') throw new AccessApiError(403,'KYC_REVIEW_DENIED');
    return {area:'kyc-review',authority:'KycReviewer'};
  }
  if (area === 'advisor') {
    const context = await new AccessApi(current).request<{
      tenantId: string; actorId: string; authority: string;
      policyVersion: number; subjectVersion: number; recordedRelationshipCount: number;
    }>('advisor/context', 'GET', undefined, signal);
    if (!validSession(current, current.tenantId) ||
        context.tenantId !== current.tenantId || context.actorId !== current.actorId ||
        context.authority !== 'FinancialAdvisor' ||
        !Number.isSafeInteger(context.policyVersion) || context.policyVersion < 1 ||
        !Number.isSafeInteger(context.subjectVersion) || context.subjectVersion !== current.subjectVersion ||
        !Number.isSafeInteger(context.recordedRelationshipCount) || context.recordedRelationshipCount < 0)
      throw new AccessApiError(503, 'ADVISOR_CONTEXT_INVALID');
    return {
      area: 'advisor', authority: 'FinancialAdvisor',
      policyVersion: context.policyVersion, subjectVersion: context.subjectVersion,
      recordedRelationshipCount: context.recordedRelationshipCount,
    };
  }
  if (area === 'product') {
    const context = await new AccessApi(current).product<ProductWorkspaceContext | null>(
      'v1/context',
      'GET',
      undefined,
      signal,
    );
    if (
      !validSession(current, current.tenantId) ||
      context?.tenantId !== current.tenantId ||
      context.actorId !== current.actorId ||
      !identifier(context.customerId, 128) ||
      !context.customerName.trim() ||
      !Array.isArray(context.capabilities) ||
      context.capabilities.some((capability) => !identifier(capability, 160)) ||
      context.setup !== undefined && (
        context.setup.tenantId !== current.tenantId || context.setup.actorId !== current.actorId || context.setup.customerId !== context.customerId ||
        ['required', 'emailVerified', 'profileReviewed', 'agreementsPublished', 'agreementsAccepted', 'ready'].some(key => typeof context.setup?.[key as keyof typeof context.setup] !== 'boolean') ||
        context.setup.ready !== (!context.setup.required || context.setup.emailVerified && context.setup.profileReviewed && context.setup.agreementsPublished && context.setup.agreementsAccepted) ||
        context.setup.agreementsAccepted && !context.setup.agreementsPublished ||
        context.setup.agreementsPublished && (typeof context.setup.revision !== 'string' || !/^[0-9a-f]{64}$/.test(context.setup.revision)) ||
        !context.setup.agreementsPublished && context.setup.revision !== null ||
        !context.setup.ready && context.capabilities.length !== 0
      ) ||
      context.productionAccepted ||
      context.dataDurability !== 'Temporary data'
    )
      throw new AccessApiError(503, 'PRODUCT_CONTEXT_INVALID');
    return { area: 'product', context };
  }
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
  return { area: 'administration', authority: 'TenantAdmin', policyVersion: context.policyVersion };
}

// Also used by isolated component hosts, without creating nested session owners.
export function WorkspaceSessionProvider({
  children,
  area = 'administration',
}: {
  children: ReactNode;
  area?: WorkspaceArea;
}) {
  const parent = useContext(Context);
  return parent ? children : <SessionOwner area={area}>{children}</SessionOwner>;
}
function SessionOwner({ children, area }: { children: ReactNode; area: WorkspaceArea }) {
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
    productContextMemory.current = null;
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
    (current: TenantSession, authorization: WorkspaceAuthorization, id: number) => {
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
      productContextMemory.current =
        authorization.area === 'product' ? authorization.context : null;
      setState({
        status: 'authorized',
        session: current,
        api,
        authorization,
        message: '',
        recovery: recovery.current,
      });
    },
    [leave],
  );
  const connectUsing = useCallback(
    async (tenant: string, issue: (signal: AbortSignal) => Promise<TenantSession>, method: 'local' | 'email' | 'password' = 'local') => {
      const id = stop();
      const controller = new AbortController();
      request.current = controller;
      candidate.current = null;
      setState({ ...empty, status: 'connecting' });
      let issued: TenantSession | null = null;
      try {
        issued = await issue(controller.signal);
        if (!validSession(issued, tenant))
          throw new AccessApiError(503, 'SESSION_RESPONSE_INVALID');
        if (id !== epoch.current || !mounted.current) return;
        setState({ ...empty, status: 'verifying' });
        const authorization = await verify(issued, area, controller.signal);
        publish(issued, authorization, id);
      } catch (failure) {
        if (id !== epoch.current || !mounted.current) return;
        candidate.current = null;
        setState({
          ...empty,
          message:
            failure instanceof AccessApiError && failure.status === 403
              ? area === 'administration'
                ? 'Organization Administrator access is required.'
                : area === 'kyc-review' ? 'Current named KYC reviewer assignment is required.' : area === 'advisor' ? 'An active Financial Advisor / Relationship Manager role is required.' : 'A customer workspace and product role are required.'
              : failure instanceof AccessApiError && failure.status === 401
                ? method === 'password'
                  ? 'Unable to sign in. Check your organization, email and password, or use Forgot password.'
                  : method === 'email'
                  ? 'The verification code is invalid, expired or already used. Request a new code if needed.'
                  : 'Unable to connect. Check your organization and local access key, then try again.'
                : failure instanceof AccessApiError && failure.status === 429
                  ? 'Too many attempts. Wait a minute before trying again.'
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
    [stop, publish, area],
  );
  const connect = useCallback((tenant: string, key: string) =>
    connectUsing(tenant, (signal) => establishSession(tenant, key, signal)), [connectUsing]);
  const connectPassword = useCallback((tenant: string, email: string, password: string) => connectUsing(tenant, signal => registrationRequest<TenantSession>('/sign-in', { tenantId: tenant, email, password }, signal), 'password'), [connectUsing]);
  const connectEmail = useCallback((tenant: string, challengeId: string, code: string) =>
    connectUsing(tenant, (signal) => confirmCustomerEmailCode(tenant, challengeId, code, signal), 'email'), [connectUsing]);
  const refreshContext = useCallback(async () => {
    const current = candidate.current;
    const signal = request.current?.signal;
    const id = epoch.current;
    if (!current || !signal || !validSession(current, current.tenantId))
      throw new AccessApiError(401, 'SESSION_EXPIRED');
    try {
      const authorization = await verify(current, area, signal);
      if (id !== epoch.current || !mounted.current) throw new AccessApiError(401, 'SESSION_CHANGED');
      productContextMemory.current = authorization.area === 'product' ? authorization.context : null;
      setState(previous => ({ ...previous, authorization }));
    } catch (failure) {
      if (id === epoch.current && mounted.current)
        leave('Your workspace could not be verified. Reconnect before continuing.', failure instanceof AccessApiError && failure.status === 503);
      throw failure;
    }
  }, [area, leave]);
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
      const authorization = await verify(current, area, controller.signal);
      publish(current, authorization, id);
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
  }, [stop, leave, publish, area]);
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
    <Context.Provider value={{ ...state, connect, connectEmail, connectPassword, refreshContext, retry, signOut, cancel }}>
      {children}
    </Context.Provider>
  );
}
