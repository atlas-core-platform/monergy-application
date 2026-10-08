import { createContext, useContext, useState } from 'react';
import type { Dispatch, SetStateAction } from 'react';
import type { TenantSession } from './accessApi';
type State = [TenantSession | null, Dispatch<SetStateAction<TenantSession | null>>];
export const Context = createContext<State | null>(null);
export const sessionMemory: { current: TenantSession | null } = { current: null };
export const localUat =
  (import.meta.env as Record<string, unknown>).VITE_MONERGY_LOCAL_UAT === 'true';
// Isolated component hosts retain their own state. The application provider keeps
// a session across SPA navigation; refresh deliberately requires sign-in again.
export function useWorkspaceSession(): State {
  const context = useContext(Context);
  const own = useState<TenantSession | null>(null);
  return context ?? own;
}
export function tenantContract<
  T extends {
    security: {
      actor: { actorId: string; authenticationContextId: string; authenticatedAt: string };
      access: { customerId: string };
    };
    payload: object;
  },
>(body: T): { body: T; headers: Record<string, string> } {
  if (!localUat) return { body, headers: {} };
  const current = sessionMemory.current;
  if (!current || Date.parse(current.expiresAt) <= Date.now())
    throw new Error('Connect your local tenant in People & access before continuing.');
  const customerId = current.tenantId === 'T001' ? 'reference-customer' : 'other-customer';
  return {
    body: {
      ...body,
      security: {
        ...body.security,
        actor: {
          ...body.security.actor,
          actorId: current.actorId,
          authenticationContextId: current.authenticationContextId,
          authenticatedAt: new Date().toISOString(),
        },
        access: { ...body.security.access, customerId },
      },
      payload: { ...body.payload, ...('customerId' in body.payload ? { customerId } : {}) },
    },
    headers: {
      'X-Monergy-Tenant': current.tenantId,
      'X-Monergy-Session': current.authenticationContextId,
    },
  };
}
