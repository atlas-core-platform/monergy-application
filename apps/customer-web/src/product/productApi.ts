import type { AccessApi } from '../access/accessApi';
import type { TrustedFinancialReport } from '../reports/types';
import type { SearchResults } from '../search/types';

export interface ProductEvidenceReceipt {
  operationId: string;
  documentId: string;
  documentVersionId: string;
  evidenceId: string;
  version: number;
  originalFileName: string;
  contentType: string;
  contentLength: number;
  contentSha256: string;
  acceptedAt: string;
  dataDurability: 'Runtime only';
}

export interface ProductEvidenceItem {
  documentId: string;
  documentVersionId: string;
  evidenceId: string;
  version: number;
  sourceName: string;
  originalFileName: string;
  contentType: string;
  contentLength: number;
  contentSha256: string;
  receivedAt: string;
  createdAt: string;
}

export interface ProductEvidenceLibrary {
  items: ProductEvidenceItem[];
  dataDurability: 'Runtime only';
}

export interface ProductEvidenceSource {
  documentId: string;
  documentVersionId: string;
  evidenceId: string;
  version: number;
  sourceName: string;
  originalFileName: string;
  contentType: string;
  contentSha256: string;
  receivedAt: string;
  dataDurability: 'Temporary data';
}

export interface ProductEvidenceVersion {
  documentVersionId: string;
  evidenceId: string;
  version: number;
  sourceName: string;
  originalFileName: string;
  contentType: string;
  contentSha256: string;
  receivedAt: string;
}

export interface ProductEvidenceVersionHistory {
  documentId: string;
  versions: ProductEvidenceVersion[];
  dataDurability: 'Temporary data';
}

export interface ProductSourceFact {
  sourceFactId: string;
  customerId: string;
  factType: string;
  label: string;
  candidateValue: number;
  currency: string;
  effectiveDate: string;
  sourceLocation: string;
  confidence: number;
  evidenceId: string;
  documentVersionId: string;
  extractionVersion: string;
  validationVersion: string;
}

export interface ProductProcessingActivityItem {
  jobId: string;
  documentVersionId: string;
  state: 'Scheduled' | 'Running' | 'CancellationRequested' | 'Completed' | 'Failed' | 'Cancelled';
  attempt: number;
  updatedAt: string;
}

export interface ProductProcessingActivityView {
  items: ProductProcessingActivityItem[];
  dataDurability: 'Temporary data';
}

export interface ProductRecoveredProcessingFact {
  sourceFactId: string;
  factType: 'INCOME' | 'EXPENSE' | 'LOAN' | 'ASSET' | 'INVESTMENT' | 'INSURANCE';
  label: string;
  candidateValue: number;
  currency: string;
  effectiveDate: string;
  sourceLocation: string;
  confidence: number;
  documentVersionId: string;
}

export interface ProductRecoveredProcessingResult {
  jobId: string;
  documentVersionId: string;
  state: 'Completed';
  updatedAt: string;
  facts: ProductRecoveredProcessingFact[];
  dataDurability: 'Temporary data';
}

export interface ProductProcessingView {
  job: {
    jobId: string;
    jobName: string;
    ownerService: string;
    payloadReference: string;
    customerId: string;
    state: 'Scheduled' | 'Running' | 'CancellationRequested' | 'Completed' | 'Failed' | 'Cancelled';
    attempt: number;
    failureCode: string | null;
    retryable: boolean;
    updatedAt: string;
  };
  processing: {
    processingId: string;
    documentVersionId: string;
    state: 'Accepted' | 'Running' | 'Completed' | 'Failed';
    failureCode: string | null;
    retryable: boolean;
    updatedAt: string;
  } | null;
  facts: ProductSourceFact[];
  dataDurability: 'Runtime only';
  processingMode: 'Structured text reference processor';
}


export interface ProductFinancialSource {
  sourceName: string;
  documentVersion: string;
  recordedAt: string;
  technicalSourceFactId: string;
  technicalProvenanceId: string;
}

export interface ProductFinancialFact {
  financialFactId: string;
  factType: 'INCOME' | 'EXPENSE' | string;
  label: string;
  value: number;
  currency: string;
  effectiveDate: string;
  revision: number;
  source: ProductFinancialSource;
}

export interface ProductFinancialProfile {
  financialProfileId: string;
  revision: number;
  updatedAt: string | null;
  facts: ProductFinancialFact[];
  dataDurability: 'Temporary data';
}

export interface ProductFinancialPromotionResult {
  profile: ProductFinancialProfile;
  confirmed: number;
}

export interface ProductFinancialFactDetail {
  current: ProductFinancialFact;
  history: ProductFinancialFact[];
}

export const evidenceTypes = ['application/pdf', 'image/png', 'image/jpeg', 'text/plain'];
export const maximumEvidenceBytes = 1_048_576;

export function listEvidence(
  api: AccessApi,
  signal?: AbortSignal,
): Promise<ProductEvidenceLibrary> {
  return api.product<ProductEvidenceLibrary>('v1/evidence', 'GET', undefined, signal);
}

export async function getEvidenceVersionHistory(
  api: AccessApi,
  documentId: string,
  signal?: AbortSignal,
): Promise<ProductEvidenceVersionHistory> {
  const history = await api.product<ProductEvidenceVersionHistory>(
    `v1/evidence/${encodeURIComponent(documentId)}/versions`,
    'GET',
    undefined,
    signal,
  );
  if (
    !history ||
    history.documentId !== documentId ||
    !Array.isArray(history.versions) ||
    history.versions.length < 1 ||
    new Set(history.versions.map((version) => version.documentVersionId)).size !== history.versions.length ||
    new Set(history.versions.map((version) => version.version)).size !== history.versions.length ||
    history.versions.some(
      (version) =>
        !version ||
        typeof version.documentVersionId !== 'string' ||
        typeof version.version !== 'number' ||
        version.version < 1,
    )
  )
    throw new Error('Document version history response is invalid.');
  return history;
}

export function getEvidenceSource(
  api: AccessApi,
  documentVersionId: string,
  signal?: AbortSignal,
): Promise<ProductEvidenceSource> {
  return api.product<ProductEvidenceSource>(
    `v1/evidence/source/${encodeURIComponent(documentVersionId)}`,
    'GET',
    undefined,
    signal,
  );
}

export async function submitEvidence(
  api: AccessApi,
  operationId: string,
  file: File,
  signal?: AbortSignal,
): Promise<ProductEvidenceReceipt> {
  const contentBase64 = await new Promise<string>((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => {
      reject(new Error('The selected file could not be read.'));
    };
    reader.onload = () => {
      const value = typeof reader.result === 'string' ? reader.result : '';
      const separator = value.indexOf(',');
      if (separator < 0) reject(new Error('The selected file could not be encoded.'));
      else resolve(value.slice(separator + 1));
    };
    reader.readAsDataURL(file);
  });
  return api.product<ProductEvidenceReceipt>(
    'v1/evidence',
    'POST',
    {
      operationId,
      originalFileName: file.name,
      contentType: file.type,
      contentBase64,
    },
    signal,
  );
}

export function scheduleDocumentProcessing(
  api: AccessApi,
  documentVersionId: string,
  operationId: string,
  signal?: AbortSignal,
): Promise<ProductProcessingView> {
  return api.product<ProductProcessingView>(
    `v1/evidence/${encodeURIComponent(documentVersionId)}/processing`,
    'POST',
    { operationId },
    signal,
  );
}

export async function getRecoveredProcessingResult(
  api: AccessApi,
  jobId: string,
  signal?: AbortSignal,
): Promise<ProductRecoveredProcessingResult> {
  const result = await api.product<ProductRecoveredProcessingResult>(
    `v1/processing-result/${encodeURIComponent(jobId)}`,
    'GET',
    undefined,
    signal,
  );
  if (
    !result ||
    result.jobId !== jobId ||
    result.state !== 'Completed' ||
    !result.documentVersionId ||
    !Array.isArray(result.facts) ||
    result.facts.length < 1 ||
    new Set(result.facts.map((fact) => fact.sourceFactId)).size !== result.facts.length ||
    result.facts.some(
      (fact) =>
        !fact ||
        (fact.factType !== 'INCOME' && fact.factType !== 'EXPENSE' && fact.factType !== 'LOAN' && fact.factType !== 'ASSET' && fact.factType !== 'INVESTMENT' && fact.factType !== 'INSURANCE') ||
        fact.documentVersionId !== result.documentVersionId ||
        fact.confidence < 0 ||
        fact.confidence > 1,
    )
  )
    throw new Error('Recovered processing result response is invalid.');
  return result;
}

export function getProcessingActivity(
  api: AccessApi,
  signal?: AbortSignal,
): Promise<ProductProcessingActivityView> {
  return api.product<ProductProcessingActivityView>(
    'v1/processing-activity',
    'GET',
    undefined,
    signal,
  );
}

export function getDocumentProcessing(
  api: AccessApi,
  jobId: string,
  signal?: AbortSignal,
): Promise<ProductProcessingView> {
  return api.product<ProductProcessingView>(
    `v1/processing/${encodeURIComponent(jobId)}`,
    'GET',
    undefined,
    signal,
  );
}

export function getFinancialProfile(
  api: AccessApi,
  signal?: AbortSignal,
): Promise<ProductFinancialProfile> {
  return api.product<ProductFinancialProfile>('v1/financial-profile', 'GET', undefined, signal);
}

export function addProcessingFactsToFinancialProfile(
  api: AccessApi,
  jobId: string,
  operationId: string,
  sourceFactIds: string[],
  signal?: AbortSignal,
): Promise<ProductFinancialPromotionResult> {
  return api.product<ProductFinancialPromotionResult>(
    `v1/financial-profile/from-processing/${encodeURIComponent(jobId)}`,
    'POST',
    { operationId, sourceFactIds },
    signal,
  );
}

export function getFinancialFactDetail(
  api: AccessApi,
  financialFactId: string,
  signal?: AbortSignal,
): Promise<ProductFinancialFactDetail> {
  return api.product<ProductFinancialFactDetail>(
    `v1/financial-profile/facts/${encodeURIComponent(financialFactId)}`,
    'GET',
    undefined,
    signal,
  );
}


export function generateBasicFinancialReport(
  api: AccessApi,
  operationId: string,
  signal?: AbortSignal,
): Promise<TrustedFinancialReport> {
  return api.product<TrustedFinancialReport>(
    'v1/reports',
    'POST',
    { operationId },
    signal,
  );
}

export function getBasicFinancialReport(
  api: AccessApi,
  reportId: string,
  signal?: AbortSignal,
): Promise<TrustedFinancialReport> {
  return api.product<TrustedFinancialReport>(
    `v1/reports/${encodeURIComponent(reportId)}`,
    'GET',
    undefined,
    signal,
  );
}


export type ProductSearchScope = 'all' | 'documents' | 'financial';

export function searchProductInformation(
  api: AccessApi,
  query: string,
  scope: ProductSearchScope,
  signal?: AbortSignal,
): Promise<SearchResults> {
  return api.product<SearchResults>(
    'v1/search',
    'POST',
    { query, scope },
    signal,
  );
}
