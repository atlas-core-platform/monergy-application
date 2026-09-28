export type SearchMatchMode = 'Lexical' | 'Semantic';
export type SearchResultType = 'Document' | 'Financial' | 'Calculation';
export interface SearchSourceReference {
  sourceObjectType: string;
  sourceObjectId: string;
  sourceReferenceId: string;
  provenanceReferenceId: string | null;
  calculationLineageReferenceId: string | null;
}
export interface SearchHit {
  searchRecordId: string;
  resultType: SearchResultType;
  title: string;
  summary: string;
  authoritativeOwner: string;
  representationState: 'DERIVED_REBUILDABLE';
  score: number;
  source: SearchSourceReference;
}
export interface SearchResults {
  query: string;
  matchMode: SearchMatchMode;
  items: SearchHit[];
}
export interface SearchFailure {
  code: string;
  message: string;
  retryable: boolean;
}
export type SearchKind = 'documents' | 'financial';
