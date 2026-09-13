export interface ReferenceRunRequest {
  customerId: string;
  sourceName: string;
  originalFileName: string;
  contentReference: string;
}

export interface ExtractedObservation {
  sourceFactId: string;
  label: string;
  candidateValue: number;
  currency: string;
  confidence: number;
  sourceLocation: string;
}

export interface AuthoritativeFinancialFact {
  financialFactId: string;
  label: string;
  value: number;
  currency: string;
  revision: number;
  financialProvenanceId: string;
}

export interface ReferenceRunResult {
  runId: string;
  processingState: 'COMPLETED';
  evidenceId: string;
  documentVersionId: string;
  observations: ExtractedObservation[];
  financialFacts: AuthoritativeFinancialFact[];
  auditRecordCount: number;
  correlationId: string;
}

export interface ReferenceRunFailure {
  code: string;
  message: string;
  retryable: boolean;
}
