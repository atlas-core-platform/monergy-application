export type LegalOutcome = 'NO_ISSUE_IDENTIFIED' | 'ACTION_REQUIRED' | 'UNABLE_TO_CONCLUDE';

export interface LegalDocument {
  code: string; label: string; owner: 'Evidence'; reference: string;
  assignedVersion: number; currentVersion: number; inspected: boolean; inspectedAt: string | null;
}
export interface LegalReviewEvidenceSnapshot {
  code: string; owner: 'Evidence'; reference: string; version: number;
}
export interface LegalReviewRevision {
  revision: number; outcome: LegalOutcome; findings: string; recordedAt: string; recordedBy: string;
  policyVersion: number; subjectVersion: number; customerVersion: number; consentVersion: number;
  evidence: LegalReviewEvidenceSnapshot[];
}
export interface LegalReview {
  reviewId: string; matterReference: string; matterLabel: string;
  state: 'Assigned' | 'Inspection in progress' | 'Reviewed'; version: number; assignedAt: string;
  evidence: LegalDocument[]; revisions: LegalReviewRevision[];
  dataDurability: 'Temporary data' | 'Durable test data';
}
export interface LegalReviewPage { tenantId: string; actorId: string; items: LegalReview[] }
export interface CustomerLegalReview {
  reviewId: string; matterReference: string; matterLabel: string; revision: number;
  outcome: LegalOutcome; findings: string; recordedAt: string; recordedBy: string; evidenceCount: number;
  dataDurability: 'Temporary data' | 'Durable test data';
}

const identifier = (value: unknown, maximum = 128): value is string =>
  typeof value === 'string' && value.length > 0 && value.length <= maximum &&
  /^[A-Za-z0-9][A-Za-z0-9_.:-]*$/.test(value);
const positive = (value: unknown): value is number => Number.isSafeInteger(value) && Number(value) > 0;
const instant = (value: unknown): value is string => typeof value === 'string' && !Number.isNaN(Date.parse(value));
const text = (value: unknown, minimum: number, maximum: number): value is string =>
  typeof value === 'string' && value.trim().length >= minimum && value.length <= maximum &&
  !Array.from(value).some(character => character.charCodeAt(0) <= 31 || character.charCodeAt(0) === 127);
const outcome = (value: unknown): value is LegalOutcome =>
  value === 'NO_ISSUE_IDENTIFIED' || value === 'ACTION_REQUIRED' || value === 'UNABLE_TO_CONCLUDE';

export function legalReview(value: unknown): LegalReview {
  if (!value || typeof value !== 'object') throw new Error('LEGAL_REVIEW_INVALID');
  const item = value as Partial<LegalReview>;
  if (!identifier(item.reviewId) || !identifier(item.matterReference, 160) || !text(item.matterLabel, 1, 120) ||
      !['Assigned', 'Inspection in progress', 'Reviewed'].includes(item.state ?? '') || !positive(item.version) ||
      !instant(item.assignedAt) || !Array.isArray(item.evidence) || item.evidence.length < 1 || item.evidence.length > 50 ||
      !Array.isArray(item.revisions) || item.revisions.length > 100 ||
      !['Temporary data', 'Durable test data'].includes(item.dataDurability ?? ''))
    throw new Error('LEGAL_REVIEW_INVALID');
  const codes = new Set<string>();
  const evidence = item.evidence.map(document => {
    if (!identifier(document.code, 64) || codes.has(document.code) || !text(document.label, 1, 120) ||
        (document as { owner?: unknown }).owner !== 'Evidence' || !identifier(document.reference, 160) ||
        !positive(document.assignedVersion) || !positive(document.currentVersion) || typeof document.inspected !== 'boolean' ||
        document.inspectedAt !== null && !instant(document.inspectedAt) || document.inspected !== (document.inspectedAt !== null))
      throw new Error('LEGAL_REVIEW_INVALID');
    codes.add(document.code); return document;
  });
  const revisions = item.revisions.map((revision, index) => {
    if (revision.revision !== index + 1 || !outcome(revision.outcome) || !text(revision.findings, 3, 2000) ||
        !instant(revision.recordedAt) || !identifier(revision.recordedBy) || !positive(revision.policyVersion) ||
        !positive(revision.subjectVersion) || !positive(revision.customerVersion) || !positive(revision.consentVersion) ||
        !Array.isArray(revision.evidence) || revision.evidence.length !== evidence.length || revision.evidence.some(snapshot =>
          !codes.has(snapshot.code) || (snapshot as { owner?: unknown }).owner !== 'Evidence' ||
          !identifier(snapshot.reference, 160) || !positive(snapshot.version)))
      throw new Error('LEGAL_REVIEW_INVALID');
    return revision;
  });
  if ((item.state === 'Reviewed') !== (revisions.length > 0)) throw new Error('LEGAL_REVIEW_INVALID');
  return { ...item, evidence, revisions } as LegalReview;
}

export function legalReviewPage(value: unknown, tenantId: string, actorId: string): LegalReviewPage {
  if (!value || typeof value !== 'object') throw new Error('LEGAL_REVIEW_PAGE_INVALID');
  const page = value as Partial<LegalReviewPage>;
  if (page.tenantId !== tenantId || page.actorId !== actorId || !Array.isArray(page.items) || page.items.length > 100)
    throw new Error('LEGAL_REVIEW_PAGE_INVALID');
  return { tenantId, actorId, items: page.items.map(legalReview) };
}

export function customerLegalReviews(value: unknown, tenantId: string, actorId: string): CustomerLegalReview[] {
  if (!value || typeof value !== 'object') throw new Error('CUSTOMER_LEGAL_REVIEWS_INVALID');
  const page = value as { tenantId?: unknown; actorId?: unknown; items?: unknown };
  if (page.tenantId !== tenantId || page.actorId !== actorId || !Array.isArray(page.items) || page.items.length > 100)
    throw new Error('CUSTOMER_LEGAL_REVIEWS_INVALID');
  return page.items.map(raw => {
    const item = raw as Partial<CustomerLegalReview>;
    if (!identifier(item.reviewId) || !identifier(item.matterReference, 160) || !text(item.matterLabel, 1, 120) ||
        !positive(item.revision) || !outcome(item.outcome) || !text(item.findings, 3, 2000) ||
        !instant(item.recordedAt) || !identifier(item.recordedBy) || !positive(item.evidenceCount) ||
        !['Temporary data', 'Durable test data'].includes(item.dataDurability ?? ''))
      throw new Error('CUSTOMER_LEGAL_REVIEWS_INVALID');
    return item as CustomerLegalReview;
  });
}

export const outcomeLabel = (value: LegalOutcome) => ({
  NO_ISSUE_IDENTIFIED: 'No issue identified', ACTION_REQUIRED: 'Action required',
  UNABLE_TO_CONCLUDE: 'Unable to conclude',
})[value];

export function operationId(): string {
  return Array.from(crypto.getRandomValues(new Uint8Array(16)), value => value.toString(16).padStart(2, '0')).join('');
}
