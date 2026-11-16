export function workspaceAreaForPath(pathname: string) {
  if (pathname === '/advisor') return 'advisor' as const;
  if (pathname === '/dsa') return 'dsa' as const;
  if (pathname === '/lender') return 'lender' as const;
  if (pathname === '/financial-professional') return 'financial-professional' as const;
  if (pathname === '/property-valuer') return 'property-valuer' as const;
  if (pathname === '/legal-advisor') return 'legal-advisor' as const;
  if (pathname === '/operations-support') return 'operations-support' as const;
  if (pathname === '/kyc/reviews') return 'kyc-review' as const;
  if (pathname === '/access' || pathname.startsWith('/access/')) return 'administration' as const;
  return 'product' as const;
}
