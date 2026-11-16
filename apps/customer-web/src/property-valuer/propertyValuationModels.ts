export interface PropertyEvidence {
  code: string; label: string; owner: 'Evidence' | 'Financial Profile'; reference: string;
  assignedVersion: number; currentVersion: number; inspected: boolean; inspectedAt: string | null;
}
export interface PropertyValuationEvidenceSnapshot {
  code: string; owner: 'Evidence' | 'Financial Profile'; reference: string; version: number;
}
export interface PropertyValuationRevision {
  revision: number; marketValue: number; currency: string; effectiveDate: string; basis: string;
  findings: string; recordedAt: string; recordedBy: string; policyVersion: number;
  subjectVersion: number; customerVersion: number; consentVersion: number;
  evidence: PropertyValuationEvidenceSnapshot[];
}
export interface PropertyValuation {
  valuationId: string; propertyReference: string; propertyLabel: string;
  state: 'Assigned' | 'Inspection in progress' | 'Valued'; version: number; assignedAt: string;
  evidence: PropertyEvidence[]; revisions: PropertyValuationRevision[];
  dataDurability: 'Temporary data' | 'Durable test data';
}
export interface PropertyValuationPage { tenantId: string; actorId: string; items: PropertyValuation[] }
export interface CustomerPropertyValuation {
  valuationId: string; propertyReference: string; propertyLabel: string; revision: number;
  marketValue: number; currency: string; effectiveDate: string; basis: string; findings: string;
  recordedAt: string; recordedBy: string; evidenceCount: number;
  dataDurability: 'Temporary data' | 'Durable test data';
}

const identifier = (value: unknown, maximum = 128): value is string =>
  typeof value === 'string' && value.length > 0 && value.length <= maximum &&
  /^[A-Za-z0-9][A-Za-z0-9_.:-]*$/.test(value);
const positive = (value: unknown): value is number => Number.isSafeInteger(value) && Number(value) > 0;
const instant = (value: unknown): value is string => typeof value === 'string' && !Number.isNaN(Date.parse(value));
const date = (value: unknown): value is string => typeof value === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(value);
const text = (value: unknown, minimum: number, maximum: number): value is string =>
  typeof value === 'string' && value.trim().length >= minimum && value.length <= maximum &&
  !Array.from(value).some(character => character.charCodeAt(0) <= 31 || character.charCodeAt(0) === 127);
const money = (value: unknown): value is number => typeof value === 'number' && Number.isFinite(value) && value > 0;

export function propertyValuation(value: unknown): PropertyValuation {
  if (!value || typeof value !== 'object') throw new Error('PROPERTY_VALUATION_INVALID');
  const item = value as Partial<PropertyValuation>;
  if (!identifier(item.valuationId) || !identifier(item.propertyReference, 160) || !text(item.propertyLabel, 1, 120) ||
      !['Assigned', 'Inspection in progress', 'Valued'].includes(item.state ?? '') || !positive(item.version) ||
      !instant(item.assignedAt) || !Array.isArray(item.evidence) || item.evidence.length < 1 || item.evidence.length > 50 ||
      !Array.isArray(item.revisions) || item.revisions.length > 100 ||
      !['Temporary data', 'Durable test data'].includes(item.dataDurability ?? ''))
    throw new Error('PROPERTY_VALUATION_INVALID');
  const codes = new Set<string>();
  const evidence = item.evidence.map(source => {
    if (!identifier(source.code, 64) || codes.has(source.code) || !text(source.label, 1, 120) ||
        !['Evidence', 'Financial Profile'].includes(source.owner) || !identifier(source.reference, 160) ||
        !positive(source.assignedVersion) || !positive(source.currentVersion) || typeof source.inspected !== 'boolean' ||
        source.inspectedAt !== null && !instant(source.inspectedAt) || source.inspected !== (source.inspectedAt !== null))
      throw new Error('PROPERTY_VALUATION_INVALID');
    codes.add(source.code); return source;
  });
  const revisions = item.revisions.map((revision, index) => {
    if (revision.revision !== index + 1 || !money(revision.marketValue) || !/^[A-Z]{3}$/.test(revision.currency) ||
        !date(revision.effectiveDate) || !text(revision.basis, 3, 500) || !text(revision.findings, 3, 2000) ||
        !instant(revision.recordedAt) || !identifier(revision.recordedBy) || !positive(revision.policyVersion) ||
        !positive(revision.subjectVersion) || !positive(revision.customerVersion) || !positive(revision.consentVersion) ||
        !Array.isArray(revision.evidence) || revision.evidence.length !== evidence.length || revision.evidence.some(snapshot =>
          !codes.has(snapshot.code) || !['Evidence', 'Financial Profile'].includes(snapshot.owner) ||
          !identifier(snapshot.reference, 160) || !positive(snapshot.version)))
      throw new Error('PROPERTY_VALUATION_INVALID');
    return revision;
  });
  if ((item.state === 'Valued') !== (revisions.length > 0)) throw new Error('PROPERTY_VALUATION_INVALID');
  return { ...item, evidence, revisions } as PropertyValuation;
}

export function propertyValuationPage(value: unknown, tenantId: string, actorId: string): PropertyValuationPage {
  if (!value || typeof value !== 'object') throw new Error('PROPERTY_VALUATION_PAGE_INVALID');
  const page = value as Partial<PropertyValuationPage>;
  if (page.tenantId !== tenantId || page.actorId !== actorId || !Array.isArray(page.items) || page.items.length > 100)
    throw new Error('PROPERTY_VALUATION_PAGE_INVALID');
  return { tenantId, actorId, items: page.items.map(propertyValuation) };
}

export function customerPropertyValuations(value: unknown, tenantId: string, actorId: string): CustomerPropertyValuation[] {
  if (!value || typeof value !== 'object') throw new Error('CUSTOMER_PROPERTY_VALUATIONS_INVALID');
  const page = value as { tenantId?: unknown; actorId?: unknown; items?: unknown };
  if (page.tenantId !== tenantId || page.actorId !== actorId || !Array.isArray(page.items) || page.items.length > 100)
    throw new Error('CUSTOMER_PROPERTY_VALUATIONS_INVALID');
  return page.items.map(raw => {
    const item = raw as Partial<CustomerPropertyValuation>;
    if (!identifier(item.valuationId) || !identifier(item.propertyReference, 160) || !text(item.propertyLabel, 1, 120) ||
        !positive(item.revision) || !money(item.marketValue) || !/^[A-Z]{3}$/.test(item.currency ?? '') ||
        !date(item.effectiveDate) || !text(item.basis, 3, 500) || !text(item.findings, 3, 2000) ||
        !instant(item.recordedAt) || !identifier(item.recordedBy) || !positive(item.evidenceCount) ||
        !['Temporary data', 'Durable test data'].includes(item.dataDurability ?? ''))
      throw new Error('CUSTOMER_PROPERTY_VALUATIONS_INVALID');
    return item as CustomerPropertyValuation;
  });
}

export function operationId(): string {
  return Array.from(crypto.getRandomValues(new Uint8Array(16)), value => value.toString(16).padStart(2, '0')).join('');
}
