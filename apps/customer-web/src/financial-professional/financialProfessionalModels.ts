export interface FinancialReviewSource {
  code: string;
  label: string;
  owner: 'Financial Profile' | 'Evidence' | 'Reporting';
  reference: string;
  assignedVersion: number;
  currentVersion: number;
  reviewed: boolean;
  reviewedAt: string | null;
}
export interface FinancialProfessionalNote {
  note: string;
  sourceCodes: string[];
  recordedAt: string;
  recordedBy: string;
}
export interface FinancialCertificationSource {
  code: string;
  owner: string;
  reference: string;
  version: number;
}
export interface FinancialCertification {
  outcome: 'Certified with no exceptions' | 'Certified with exceptions' | 'Unable to certify';
  rationale: string;
  certifiedAt: string;
  certifiedBy: string;
  policyVersion: number;
  subjectVersion: number;
  customerVersion: number;
  consentVersion: number;
  sources: FinancialCertificationSource[];
}
export interface FinancialProfessionalReview {
  reviewId: string;
  reviewReference: string;
  state: 'Assigned' | 'Under review' | 'Certified';
  version: number;
  assignedAt: string;
  sources: FinancialReviewSource[];
  notes: FinancialProfessionalNote[];
  certification: FinancialCertification | null;
  dataDurability: 'Temporary data' | 'Durable test data';
}
export interface FinancialProfessionalReviewPage {
  tenantId: string;
  actorId: string;
  items: FinancialProfessionalReview[];
}

const identifier = (value: unknown, maximum: number): value is string =>
  typeof value === 'string' && value.length <= maximum && /^[A-Za-z0-9][A-Za-z0-9_.:-]*$/.test(value);
const positive = (value: unknown): value is number =>
  typeof value === 'number' && Number.isSafeInteger(value) && value > 0;
const instant = (value: unknown): value is string =>
  typeof value === 'string' && Number.isFinite(Date.parse(value));

export function financialProfessionalReview(value: unknown): FinancialProfessionalReview {
  if (!value || typeof value !== 'object') throw new Error('FINANCIAL_REVIEW_INVALID');
  const item = value as Partial<FinancialProfessionalReview>;
  if (!identifier(item.reviewId, 128) || typeof item.reviewReference !== 'string' ||
      !item.reviewReference.trim() || item.reviewReference.length > 120 ||
      !['Assigned', 'Under review', 'Certified'].includes(item.state ?? '') || !positive(item.version) ||
      !instant(item.assignedAt) || !Array.isArray(item.sources) || item.sources.length < 1 || item.sources.length > 50 ||
      !Array.isArray(item.notes) || item.notes.length > 100 ||
      !['Temporary data', 'Durable test data'].includes(item.dataDurability ?? ''))
    throw new Error('FINANCIAL_REVIEW_INVALID');
  const codes = new Set<string>();
  const sources = item.sources.map(source => {
    if (!identifier(source.code, 64) || codes.has(source.code) || typeof source.label !== 'string' ||
        !source.label.trim() || source.label.length > 120 ||
        !['Financial Profile', 'Evidence', 'Reporting'].includes(source.owner) || !identifier(source.reference, 160) ||
        !positive(source.assignedVersion) || !positive(source.currentVersion) || typeof source.reviewed !== 'boolean' ||
        source.reviewedAt !== null && !instant(source.reviewedAt) || source.reviewed !== (source.reviewedAt !== null))
      throw new Error('FINANCIAL_REVIEW_INVALID');
    codes.add(source.code);
    return source;
  });
  const notes = item.notes.map(note => {
    if (typeof note.note !== 'string' || note.note.trim().length < 3 || note.note.length > 2000 ||
        !Array.isArray(note.sourceCodes) || note.sourceCodes.length < 1 || note.sourceCodes.length > 20 ||
        note.sourceCodes.some(code => !codes.has(code)) || !instant(note.recordedAt) || !identifier(note.recordedBy, 128))
      throw new Error('FINANCIAL_REVIEW_INVALID');
    return note;
  });
  let certification: FinancialCertification | null = null;
  if (item.certification !== null) {
    const current = item.certification;
    if (!current || !['Certified with no exceptions', 'Certified with exceptions', 'Unable to certify'].includes(current.outcome) ||
        typeof current.rationale !== 'string' || current.rationale.trim().length < 3 || current.rationale.length > 2000 ||
        !instant(current.certifiedAt) || !identifier(current.certifiedBy, 128) || !positive(current.policyVersion) ||
        !positive(current.subjectVersion) || !positive(current.customerVersion) || !positive(current.consentVersion) ||
        !Array.isArray(current.sources) || current.sources.length !== sources.length || current.sources.some(source =>
          !codes.has(source.code) || !['Financial Profile', 'Evidence', 'Reporting'].includes(source.owner) ||
          !identifier(source.reference, 160) || !positive(source.version)) ||
        item.state !== 'Certified') throw new Error('FINANCIAL_REVIEW_INVALID');
    certification = current;
  } else if (item.state === 'Certified') throw new Error('FINANCIAL_REVIEW_INVALID');
  return { ...item, sources, notes, certification } as FinancialProfessionalReview;
}

export function financialProfessionalReviewPage(value: unknown, tenantId: string, actorId: string): FinancialProfessionalReviewPage {
  if (!value || typeof value !== 'object') throw new Error('FINANCIAL_REVIEW_PAGE_INVALID');
  const page = value as Partial<FinancialProfessionalReviewPage>;
  if (page.tenantId !== tenantId || page.actorId !== actorId || !Array.isArray(page.items) || page.items.length > 100)
    throw new Error('FINANCIAL_REVIEW_PAGE_INVALID');
  return { tenantId, actorId, items: page.items.map(financialProfessionalReview) };
}

export function operationId(): string {
  return Array.from(crypto.getRandomValues(new Uint8Array(16)), value => value.toString(16).padStart(2, '0')).join('');
}
