import { useCallback, useEffect, useState } from 'react';
import type { ReactNode, SetStateAction } from 'react';
import type { TenantSession } from './accessApi';
import { Context, sessionMemory } from './workspaceSession';
export function WorkspaceSessionProvider({ children }: { children: ReactNode }) {
  const [session, update] = useState<TenantSession | null>(null);
  useEffect(
    () => () => {
      sessionMemory.current = null;
    },
    [],
  );
  const setSession = useCallback((value: SetStateAction<TenantSession | null>) => {
    const next = typeof value === 'function' ? value(sessionMemory.current) : value;
    sessionMemory.current = next;
    update(next);
  }, []);
  return <Context.Provider value={[session, setSession]}>{children}</Context.Provider>;
}
