export interface KycEvidence { documentVersionId: string; purpose: 'IDENTITY' | 'ADDRESS'; fileName: string; sha256: string }
export interface KycCase { tenantId: string; customerId: string; customerName: string; ownerActorId: string; version: number; state: string; reviewerId: string; reviewerName: string; policyRevision: string; notice: string; evidence: KycEvidence[]; reason: string; recordedBy: string; recordedAt: string; operationId: string }
export interface KycPolicy { revision: string; notice: string; reviewers: { actorId: string; name: string }[] }
export interface KycView { tenantId: string; customerId: string; policy: KycPolicy | null; current: KycCase | null }
export interface KycChange { operationId: string; expectedVersion: number; action: string; reviewerId: string | null; policyRevision: string | null; consent: boolean; reason: string; evidence: KycEvidence[] }
export const kycStates: Record<string,string> = { SUBMITTED: 'Awaiting review', WITHDRAWN: 'Withdrawn', MANUAL_REVIEW_ACCEPTED: 'Manual review accepted', CHANGES_REQUESTED: 'Changes requested', REJECTED: 'Review declined' };
const object = (x: unknown): x is Record<string,unknown> => !!x && typeof x === 'object' && !Array.isArray(x);
const id = (x: unknown) => typeof x === 'string' && /^[A-Za-z0-9][A-Za-z0-9_.:-]{0,159}$/.test(x);
const hash = (x: unknown) => typeof x === 'string' && /^[a-f0-9]{64}$/.test(x);
export function validKycCase(x: unknown,tenant: string,customer?: string): x is KycCase {
  if (!object(x) || x.tenantId !== tenant || !id(x.customerId) || customer !== undefined && x.customerId !== customer || !id(x.ownerActorId) || !id(x.reviewerId) || x.ownerActorId === x.reviewerId || !Number.isSafeInteger(x.version) || Number(x.version) < 1 || typeof x.state !== 'string' || !(x.state in kycStates) || !hash(x.policyRevision) || !id(x.recordedBy) || typeof x.recordedAt !== 'string' || !Number.isFinite(Date.parse(x.recordedAt)) || typeof x.operationId !== 'string' || !/^[a-f0-9]{32}$/.test(x.operationId)) return false;
  return ['customerName','reviewerName','notice','reason'].every(k => typeof x[k] === 'string' && x[k].length > 0 && x[k].length <= 4000) && Array.isArray(x.evidence) && x.evidence.length > 0 && x.evidence.length <= 3 && x.evidence.every((e: unknown) => object(e) && id(e.documentVersionId) && (e.purpose === 'IDENTITY' || e.purpose === 'ADDRESS') && typeof e.fileName === 'string' && e.fileName.length > 0 && hash(e.sha256));
}
export function validKycView(x: unknown,tenant: string,customer: string): x is KycView {
  if (!object(x) || x.tenantId !== tenant || x.customerId !== customer || x.current !== null && !validKycCase(x.current,tenant,customer)) return false;
  const p=x.policy;return p===null || object(p) && hash(p.revision) && typeof p.notice === 'string' && p.notice.length >=20 && p.notice.length <=4000 && Array.isArray(p.reviewers) && p.reviewers.length >0 && p.reviewers.length <=20 && p.reviewers.every((r: unknown)=>object(r) && id(r.actorId) && typeof r.name==='string' && r.name.length>0);
}
