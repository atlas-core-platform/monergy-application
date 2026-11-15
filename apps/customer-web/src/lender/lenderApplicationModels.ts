export interface LenderDocument {
  code: string;
  label: string;
  present: boolean;
  reviewed: boolean;
  reviewedAt: string | null;
}
export interface LenderDecision {
  decision: 'Approved' | 'Declined' | 'More information required';
  reason: string;
  decidedAt: string;
}
export interface LenderApplication {
  applicationId: string;
  applicationReference: string;
  state: 'Assigned' | 'Under review' | 'Decided';
  version: number;
  assignedAt: string;
  requiredDocuments: LenderDocument[];
  decision: LenderDecision | null;
  dataDurability: 'Temporary data' | 'Durable test data';
}
export interface LenderApplicationPage {
  tenantId: string;
  actorId: string;
  items: LenderApplication[];
}

const identifier = (value: unknown, maximum: number): value is string =>
  typeof value === 'string' && value.length <= maximum &&
  /^[A-Za-z0-9][A-Za-z0-9_.:-]*$/.test(value);
const instant = (value: unknown): value is string =>
  typeof value === 'string' && Number.isFinite(Date.parse(value));

export function lenderApplication(value: unknown): LenderApplication {
  if (!value || typeof value !== 'object') throw new Error('LENDER_APPLICATION_INVALID');
  const item = value as Partial<LenderApplication>;
  if (!identifier(item.applicationId, 128) || typeof item.applicationReference !== 'string' ||
      !item.applicationReference.trim() || item.applicationReference.length > 120 ||
      !['Assigned', 'Under review', 'Decided'].includes(item.state ?? '') ||
      typeof item.version !== 'number' || !Number.isSafeInteger(item.version) || item.version < 1 || !instant(item.assignedAt) ||
      !Array.isArray(item.requiredDocuments) || item.requiredDocuments.length < 1 || item.requiredDocuments.length > 50 ||
      !['Temporary data', 'Durable test data'].includes(item.dataDurability ?? ''))
    throw new Error('LENDER_APPLICATION_INVALID');
  const codes = new Set<string>();
  const requiredDocuments = item.requiredDocuments.map(document => {
    if (!identifier(document.code, 64) || typeof document.label !== 'string' ||
        !document.label.trim() || document.label.length > 120 || typeof document.present !== 'boolean' ||
        typeof document.reviewed !== 'boolean' || document.reviewed && !document.present ||
        document.reviewedAt !== null && !instant(document.reviewedAt) || document.reviewed !== (document.reviewedAt !== null) ||
        codes.has(document.code)) throw new Error('LENDER_APPLICATION_INVALID');
    codes.add(document.code);
    return document;
  });
  let decision: LenderDecision | null = null;
  if (item.decision !== null) {
    const current = item.decision;
    if (!current || !['Approved', 'Declined', 'More information required'].includes(current.decision) ||
        typeof current.reason !== 'string' || current.reason.trim().length < 3 || current.reason.length > 600 ||
        !instant(current.decidedAt) || item.state !== 'Decided') throw new Error('LENDER_APPLICATION_INVALID');
    decision = current;
  } else if (item.state === 'Decided') throw new Error('LENDER_APPLICATION_INVALID');
  return { ...item, requiredDocuments, decision } as LenderApplication;
}

export function lenderApplicationPage(value: unknown, tenantId: string, actorId: string): LenderApplicationPage {
  if (!value || typeof value !== 'object') throw new Error('LENDER_APPLICATION_PAGE_INVALID');
  const page = value as Partial<LenderApplicationPage>;
  if (page.tenantId !== tenantId || page.actorId !== actorId || !Array.isArray(page.items) || page.items.length > 100)
    throw new Error('LENDER_APPLICATION_PAGE_INVALID');
  return { tenantId, actorId, items: page.items.map(lenderApplication) };
}

export function operationId(): string {
  return Array.from(crypto.getRandomValues(new Uint8Array(16)), value => value.toString(16).padStart(2, '0')).join('');
}
