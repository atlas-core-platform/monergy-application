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
  extractionVersion?: string;
  sourceFactId: string;
  factType: 'INCOME' | 'EXPENSE' | 'LOAN' | 'ASSET' | 'INVESTMENT' | 'INSURANCE' | 'TAX';
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
  processingMode: 'Document extraction' | 'Structured text reference processor';
}


export interface ProductFinancialSource {
  sourceName: string;
  documentVersion: string;
  recordedAt: string;
  technicalSourceFactId: string;
  technicalProvenanceId: string;
}

export interface ProductAssetTypeSelection {
  sourceFactId: string;
  assetType: 'PROPERTY' | 'VEHICLE';
}

export interface ProductFinancialFact {
  financialFactId: string;
  factType: string;
  assetType?: 'PROPERTY' | 'VEHICLE';
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

export const financialXlsxType = 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';
export const evidenceTypes = ['application/pdf', 'image/png', 'image/jpeg', 'text/plain', 'text/csv', financialXlsxType];
export const financialCsvTemplate = 'Category,Description,Amount,Currency,Date,Source\r\nINCOME,Monthly salary,125000.00,INR,2026-10-01,Salary statement\r\n';
export function evidenceContentType(file: Pick<File, 'name' | 'type'>): string {
  if (/\.xlsx$/i.test(file.name) && ['', financialXlsxType, 'application/zip', 'application/octet-stream'].includes(file.type)) return financialXlsxType;
  return /\.csv$/i.test(file.name) && ['', 'text/csv', 'application/vnd.ms-excel'].includes(file.type)
    ? 'text/csv'
    : file.type;
}
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
  // Owner responses need runtime validation even when a TypeScript transport type is declared.
  /* eslint-disable @typescript-eslint/no-unnecessary-condition, @typescript-eslint/prefer-optional-chain */
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
  /* eslint-enable @typescript-eslint/no-unnecessary-condition, @typescript-eslint/prefer-optional-chain */
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
      contentType: evidenceContentType(file),
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
  // Owner responses need runtime validation even when a TypeScript transport type is declared.
  /* eslint-disable @typescript-eslint/no-unnecessary-condition, @typescript-eslint/prefer-optional-chain */
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
        (fact.factType !== 'INCOME' && fact.factType !== 'EXPENSE' && fact.factType !== 'LOAN' && fact.factType !== 'ASSET' && fact.factType !== 'INVESTMENT' && fact.factType !== 'INSURANCE' && fact.factType !== 'TAX') ||
        fact.documentVersionId !== result.documentVersionId ||
        fact.confidence < 0 ||
        fact.confidence > 1,
    )
  )
    throw new Error('Recovered processing result response is invalid.');
  /* eslint-enable @typescript-eslint/no-unnecessary-condition, @typescript-eslint/prefer-optional-chain */
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
  assetTypes?: ProductAssetTypeSelection[],
): Promise<ProductFinancialPromotionResult> {
  return api.product<ProductFinancialPromotionResult>(
    `v1/financial-profile/from-processing/${encodeURIComponent(jobId)}`,
    'POST',
    { operationId, sourceFactIds, ...(assetTypes?.length ? { assetTypes } : {}) },
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

export interface ProductLoanDraft {
  name: string; lender: string; loanType: string; sanctionAmount: number; outstanding: number;
  interestRatePercent: number; emi: number; tenureMonths: number; startDate: string; endDate: string;
  balanceAsOfDate: string; sourceFinancialFactId: string; sourceRevision: number;
}
export interface ProductLoanRecord {
  loanRecordId: string; operationId: string; customerId: string; loan: ProductLoanDraft; currency: string;
  sourceProvenanceId: string; documentVersionId: string; evidenceId: string; recordedBy: string; recordedAt: string; revision: number;
  lifecycleStatus: 'UNREVIEWED' | 'ACTIVE' | 'CLOSED'; lifecycleAsOfDate: string | null;
}
export interface ProductLoanRecordList { items: ProductLoanRecord[]; hasMore: boolean; dataDurability: string }
export const getLoanRecords = (api: AccessApi, signal?: AbortSignal) => api.product<ProductLoanRecordList>('v1/loan-records', 'GET', undefined, signal);
export const createLoanRecord = (api: AccessApi, input: { operationId: string; loan: ProductLoanDraft }, signal?: AbortSignal) => api.product<ProductLoanRecord>('v1/loan-records', 'POST', input, signal);
export interface ProductLoanRecordHistory { items: ProductLoanRecord[]; hasMore: boolean; nextBeforeRevision: number | null }
export const reviseLoanRecord = (api: AccessApi, id: string, input: { operationId: string; expectedRevision: number; loan: ProductLoanDraft }) =>
  api.product<ProductLoanRecord>('v1/loan-records/' + encodeURIComponent(id) + '/corrections', 'POST', input);
export interface ProductLoanStatusReview { operationId: string; expectedRevision: number; status: 'ACTIVE' | 'CLOSED'; asOfDate: string }
export const reviewLoanRecordStatus = (api: AccessApi, id: string, input: ProductLoanStatusReview) =>
  api.product<ProductLoanRecord>('v1/loan-records/' + encodeURIComponent(id) + '/lifecycle', 'POST', input);
export const getLoanRecordHistory = (api: AccessApi, id: string, beforeRevision?: number, signal?: AbortSignal) =>
  api.product<ProductLoanRecordHistory>('v1/loan-records/' + encodeURIComponent(id) + '/history' + (beforeRevision === undefined ? '' : '?beforeRevision=' + String(beforeRevision)), 'GET', undefined, signal);


export interface ProductInvestmentDraft {
  name: string; institution: string; investmentType: string; purchaseValue: number; currentValue: number;
  valueAsOfDate: string; sourceFinancialFactId: string; sourceRevision: number;
}
export interface ProductInvestmentRecord {
  investmentRecordId: string; operationId: string; customerId: string; investment: ProductInvestmentDraft; currency: string;
  sourceProvenanceId: string; documentVersionId: string; evidenceId: string; recordedBy: string; recordedAt: string; revision: number;
}
export interface ProductInvestmentRecordList { items: ProductInvestmentRecord[]; hasMore: boolean; dataDurability: string }
export const getInvestmentRecords = (api: AccessApi, signal?: AbortSignal) => api.product<ProductInvestmentRecordList>('v1/investment-records', 'GET', undefined, signal);
export const createInvestmentRecord = (api: AccessApi, input: { operationId: string; investment: ProductInvestmentDraft }, signal?: AbortSignal) =>
  api.product<ProductInvestmentRecord>('v1/investment-records', 'POST', input, signal);

export interface ProductInvestmentRecordHistory { items: ProductInvestmentRecord[]; hasMore: boolean; nextBeforeRevision: number | null }
export const reviseInvestmentRecord = (api: AccessApi, id: string, input: { operationId: string; expectedRevision: number; investment: ProductInvestmentDraft }) =>
  api.product<ProductInvestmentRecord>('v1/investment-records/' + encodeURIComponent(id) + '/corrections', 'POST', input);
export const getInvestmentRecordHistory = (api: AccessApi, id: string, beforeRevision?: number, signal?: AbortSignal) =>
  api.product<ProductInvestmentRecordHistory>('v1/investment-records/' + encodeURIComponent(id) + '/history' + (beforeRevision === undefined ? '' : '?beforeRevision=' + String(beforeRevision)), 'GET', undefined, signal);


export interface ProductIncomeDraft {
  name: string; incomeSource: string; incomeType: string; amount: number; frequency: string; financialYear: string;
  periodStart: string; periodEnd: string; sourceFinancialFactId: string; sourceRevision: number;
}
export interface ProductIncomeRecord {
  incomeRecordId: string; operationId: string; customerId: string; income: ProductIncomeDraft; currency: string;
  sourceProvenanceId: string; documentVersionId: string; evidenceId: string; recordedBy: string; recordedAt: string;
  verificationStatus: string; revision: number;
}
export interface ProductIncomeRecordList { items: ProductIncomeRecord[]; hasMore: boolean; dataDurability: string }
export const getIncomeRecords = (api: AccessApi, signal?: AbortSignal) => api.product<ProductIncomeRecordList>('v1/income-records', 'GET', undefined, signal);
export const createIncomeRecord = (api: AccessApi, input: { operationId: string; income: ProductIncomeDraft }, signal?: AbortSignal) =>
  api.product<ProductIncomeRecord>('v1/income-records', 'POST', input, signal);

export interface ProductIncomeRecordHistory { items: ProductIncomeRecord[]; hasMore: boolean; nextBeforeRevision: number | null }
export const reviseIncomeRecord = (api: AccessApi, id: string, input: { operationId: string; expectedRevision: number; income: ProductIncomeDraft }) =>
  api.product<ProductIncomeRecord>('v1/income-records/' + encodeURIComponent(id) + '/corrections', 'POST', input);
export const getIncomeRecordHistory = (api: AccessApi, id: string, beforeRevision?: number, signal?: AbortSignal) =>
  api.product<ProductIncomeRecordHistory>('v1/income-records/' + encodeURIComponent(id) + '/history' + (beforeRevision === undefined ? '' : '?beforeRevision=' + String(beforeRevision)), 'GET', undefined, signal);
