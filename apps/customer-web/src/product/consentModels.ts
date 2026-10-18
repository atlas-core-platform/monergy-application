export interface DelegatedConsent {
  id: string;
  customerId: string;
  actorId: string;
  recipientLabel?: string | null;
  purpose: string;
  capabilityIds: string[];
  expiresAt: string;
  revokedAt: string | null;
}
export interface ConsentOverview {
  version: number;
  items: DelegatedConsent[];
}
export interface ConsentRevocationReceipt {
  id: string;
  customerId: string;
  actorId: string;
  operation: 'Revoked';
  version: number;
  evidenceReference: string;
}
export function validRevocationReceipt(value: unknown): value is ConsentRevocationReceipt {
  if (!value || typeof value !== 'object') return false;
  const v = value as Partial<ConsentRevocationReceipt>;
  return typeof v.id === 'string' && typeof v.customerId === 'string' && typeof v.actorId === 'string' &&
    v.operation === 'Revoked' && Number.isSafeInteger(v.version) && (v.version ?? 0) >= 1 &&
    typeof v.evidenceReference === 'string' && v.evidenceReference.trim().length > 0;
}
export function validOverview(value: unknown, customerId: string): value is ConsentOverview {
  if (!value || typeof value !== 'object') return false;
  const data = value as { version?: number; items?: unknown[] };
  return (
    Number.isSafeInteger(data.version) &&
    (data.version ?? 0) >= 1 &&
    Array.isArray(data.items) &&
    data.items.length <= 100 &&
    data.items.every((value) => {
      if (!value || typeof value !== 'object') return false;
      const item = value as Partial<DelegatedConsent>;
      return (
        typeof item.id === 'string' &&
        item.id.length > 0 &&
        item.customerId === customerId &&
        typeof item.actorId === 'string' &&
        item.actorId.length > 0 &&
        item.purpose === 'customer-advice' &&
        (item.recipientLabel === undefined || item.recipientLabel === null ||
         (typeof item.recipientLabel === 'string' &&
          item.recipientLabel.trim().length > 0 && item.recipientLabel.length <= 320)) &&
        Array.isArray(item.capabilityIds) &&
        item.capabilityIds.every((capability) => typeof capability === 'string') &&
        typeof item.expiresAt === 'string' &&
        Number.isFinite(Date.parse(item.expiresAt)) &&
        (item.revokedAt === null ||
          (typeof item.revokedAt === 'string' && Number.isFinite(Date.parse(item.revokedAt))))
      );
    }) && new Set(data.items.map(item => (item as DelegatedConsent).id)).size === data.items.length
  );
}
