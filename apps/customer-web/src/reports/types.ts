export interface ReportSourceReference {
  sourceType: string;
  sourceId: string;
  authoritativeOwner: string;
  evidenceReferenceId: string | null;
  financialProvenanceReferenceId: string | null;
  calculationLineageReferenceId: string | null;
  ruleId?: string | null;
  ruleVersion?: string | null;
  definitionHash?: string | null;
  numericSemantics?: string | null;
  sourceCalculationIds?: string[] | null;
  financialFactReferenceId?: string | null;
  financialFactRevision?: number | null;
  documentVersionReferenceId?: string | null;
  financialGoal?: {
    revision: number;
    operationId: string;
    targetDate: string;
    lifecycleStatus: 'IN_PROGRESS' | 'PAUSED' | 'REPORTED_ACHIEVED' | 'STOPPED';
    lifecycleAsOfDate: string | null;
    priorityLevel: 'NOT_SET' | 'LOW' | 'MEDIUM' | 'HIGH';
    priorityAsOfDate: string | null;
    recordedAt: string;
  } | null;
}

export interface FinancialReviewReceiptSelection {
  assetAllocationCalculationId: string;
  monthlyNetCashflowRateCalculationId: string;
  monthlyEmiShareCalculationId: string;
  monthlyDebtServiceCoverageCalculationId: string;
}

export interface ReportLineItem {
  label: string;
  value: number;
  unit: string;
  source: ReportSourceReference;
}

export interface ReportExport {
  fileName: string;
  mediaType: string;
  content: string;
  sha256: string;
}

export interface TrustedFinancialReport {
  reportId: string;
  customerId: string;
  state: 'GENERATED';
  generatedAt: string;
  items: ReportLineItem[];
  sourceFinancialReferences: string[];
  evidenceReferences: string[];
  financialProvenanceReferences: string[];
  calculationLineageReferences: string[];
  aiResponseTraceReference: string | null;
  auditCompatibilityReferenceId: string;
  export: ReportExport;
}

export interface ReportGenerationJob {
  jobId: string;
  reportId: string;
  state: 'Scheduled' | 'Running' | 'CancellationRequested' | 'Completed' | 'Failed' | 'Cancelled';
  attempt: number;
  failureCode: string | null;
  retryable: boolean;
  updatedAt: string;
}

export interface ReportingFailure {
  code: string;
  message: string;
  retryable: boolean;
}
