import { createContext, useContext } from 'react';
import type { AccessApi, TenantSession } from './accessApi';
export interface ProductWorkspaceContext {
  tenantId: string;
  actorId: string;
  customerId: string;
  customerName: string;
  customerDetail?: string | null;
  capabilities: string[];
  environment: string;
  dataDurability: string;
  productionAccepted: boolean;
}
export type WorkspaceAuthorization =
  | { area: 'administration'; authority: 'TenantAdmin'; policyVersion: number }
  | { area: 'product'; context: ProductWorkspaceContext };
export interface WorkspaceSessionState {
  status: 'public' | 'connecting' | 'verifying' | 'authorized' | 'disconnected';
  session: TenantSession | null;
  api: AccessApi | null;
  authorization: WorkspaceAuthorization | null;
  message: string;
  recovery: { tenantId: string; actorId: string; operationId: string } | null;
  connect: (tenantId: string, key: string) => Promise<void>;
  connectEmail?: (tenantId: string, challengeId: string, code: string) => Promise<void>;
  retry: () => Promise<void>;
  signOut: () => Promise<void>;
  cancel: () => void;
}
export const Context = createContext<WorkspaceSessionState | null>(null);
export const sessionMemory: { current: TenantSession | null } = { current: null };
export const productContextMemory: { current: ProductWorkspaceContext | null } = { current: null };
export const localUat =
  (import.meta.env as Record<string, unknown>).VITE_MONERGY_LOCAL_UAT === 'true';
export function useWorkspaceSession(): WorkspaceSessionState {
  const context = useContext(Context);
  if (!context) throw new Error('WorkspaceSessionProvider is required.');
  return context;
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
  if (
    !current ||
    !Number.isFinite(Date.parse(current.expiresAt)) ||
    Date.parse(current.expiresAt) <= Date.now()
  )
    throw new Error('Connect to your Monergy workspace before continuing.');
  const product = productContextMemory.current;
  if (product?.tenantId !== current.tenantId || product.actorId !== current.actorId)
    throw new Error('Connect to your Monergy workspace before continuing.');
  const customerId = product.customerId;
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
