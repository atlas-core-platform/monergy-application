import type { AccessApi } from '../access/accessApi';
import type { ProductEvidenceItem } from './productApi';

export const vaultCategories = ['Identity', 'Income Tax', 'Banking', 'Credit', 'Loans', 'Insurance', 'Investments', 'Property', 'Vehicles', 'Other'] as const;
export interface VaultMetadata {
  category: string; documentType: string | null; documentDate: string | null; financialYear: string | null;
  revision: number; updatedBy: string | null; updatedAt: string | null;
}
export interface VaultItem extends ProductEvidenceItem {
  metadata: VaultMetadata; scanStatus: 'VERIFIED_AT_INTAKE' | 'UNRECORDED'; verificationStatus: 'NOT_VERIFIED';
  sourceLocation: { provider: string; folderId: string; fileId: string; fileVersion: string } | null;
  storageMode: 'TEMPORARY_REFERENCE' | 'OWNER_MANAGED'; storageLocation: string | null; storageVersionId: string | null;
}
export interface VaultPage { items: VaultItem[]; nextCursor: string | null }
export interface VaultMetadataChange {
  operationId: string; expectedRevision: number; category: string; documentType: string | null;
  documentDate: string | null; financialYear: string | null;
}
export interface VaultReplacement {
  operationId: string; expectedVersionId: string; originalFileName: string; contentType: string; contentBase64: string;
}
const object = (value: unknown): value is Record<string, unknown> => typeof value === 'object' && value !== null && !Array.isArray(value);
const nullableText = (value: unknown) => value === null || typeof value === 'string';
function metadata(value: unknown): value is VaultMetadata {
  return object(value) && vaultCategories.some(x => x === value.category) &&
    Number.isSafeInteger(value.revision) && (value.revision as number) >= 0 &&
    ['documentType', 'documentDate', 'financialYear', 'updatedBy', 'updatedAt'].every(key => nullableText(value[key]));
}
function item(value: unknown): value is VaultItem {
  return object(value) && ['documentId', 'documentVersionId', 'evidenceId', 'originalFileName', 'contentType', 'contentSha256', 'sourceName', 'createdAt', 'receivedAt'].every(key => typeof value[key] === 'string' && value[key].length > 0) &&
    Number.isSafeInteger(value.version) && (value.version as number) > 0 && Number.isSafeInteger(value.contentLength) && (value.contentLength as number) >= 0 && metadata(value.metadata) &&
    (value.scanStatus === 'VERIFIED_AT_INTAKE' || value.scanStatus === 'UNRECORDED') && value.verificationStatus === 'NOT_VERIFIED' &&
    (value.storageMode === 'TEMPORARY_REFERENCE' || value.storageMode === 'OWNER_MANAGED') && nullableText(value.storageLocation) && nullableText(value.storageVersionId) && (value.sourceLocation === null || object(value.sourceLocation) && ['provider', 'folderId', 'fileId', 'fileVersion'].every(key => typeof (value.sourceLocation as Record<string, unknown>)[key] === 'string')); 
}
export async function listVault(api: AccessApi, category: string, search: string, after: string | null, signal: AbortSignal): Promise<VaultPage> {
  const query = new URLSearchParams({ category, search }); if (after) query.set('after', after);
  const result = await api.product<unknown>(`v1/document-vault?${query.toString()}`, 'GET', undefined, signal);
  if (!object(result) || !Array.isArray(result.items) || result.items.length > 50 || !result.items.every(item) || !nullableText(result.nextCursor) ||
      new Set(result.items.map(x => x.documentVersionId)).size !== result.items.length ||
      result.nextCursor !== null && (result.items.length !== 50 || result.items[49]?.documentVersionId !== result.nextCursor)) throw new Error('The document list could not be verified. Refresh to try again.');
  return result as unknown as VaultPage;
}
export async function readVault(api: AccessApi, version: string, signal: AbortSignal): Promise<VaultItem> {
  const result = await api.product<unknown>(`v1/document-vault/${encodeURIComponent(version)}`, 'GET', undefined, signal);
  if (!item(result) || result.documentVersionId !== version) throw new Error('The document details could not be verified.');
  return result;
}
export async function saveVaultMetadata(api: AccessApi, version: string, change: VaultMetadataChange, signal: AbortSignal): Promise<VaultMetadata> {
  const result = await api.product<unknown>(`v1/document-vault/${encodeURIComponent(version)}/metadata`, 'PUT', change, signal);
  if (!metadata(result) || result.revision !== change.expectedRevision + 1 || result.category !== change.category || result.documentType !== change.documentType || result.documentDate !== change.documentDate || result.financialYear !== change.financialYear) throw new Error('The save result is uncertain. Retry the same operation to recover its receipt.');
  return result;
}
export async function replaceVault(api: AccessApi, document: string, change: VaultReplacement, signal: AbortSignal): Promise<string> {
  const result = await api.product<unknown>(`v1/document-vault/${encodeURIComponent(document)}/replacement`, 'POST', change, signal);
  if (!object(result) || result.documentId !== document || result.documentVersionId !== `document-version-${change.operationId}` || !Number.isSafeInteger(result.version) || (result.version as number) < 2) throw new Error('The replacement result is uncertain. Retry the same operation to recover its receipt.');
  return result.documentVersionId;
}
