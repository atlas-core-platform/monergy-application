export interface ReportSourceReference {
  sourceType: string;
  sourceId: string;
  authoritativeOwner: string;
  evidenceReferenceId: string | null;
  financialProvenanceReferenceId: string | null;
  calculationLineageReferenceId: string | null;
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

export interface ReportingFailure {
  code: string;
  message: string;
  retryable: boolean;
}
