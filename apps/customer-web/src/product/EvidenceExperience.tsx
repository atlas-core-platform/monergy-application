import { WorkspaceIcon } from '@monergy/ui-foundation';
import { Alert, Button, Checkbox, Drawer, Input, Select, Skeleton, Spin, Tag, Upload } from 'antd';
import type { UploadProps } from 'antd';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { AccessApiError } from '../access/accessApi';
import { useWorkspaceSession } from '../access/workspaceSession';
import {
  addProcessingFactsToFinancialProfile,
  evidenceTypes,
  getDocumentProcessing,
  getEvidenceSource,
  getEvidenceVersionHistory,
  getFinancialProfile,
  getProcessingActivity,
  getRecoveredProcessingResult,
  listEvidence,
  maximumEvidenceBytes,
  scheduleDocumentProcessing,
  submitEvidence,
  type ProductEvidenceItem,
  type ProductEvidenceReceipt,
  type ProductEvidenceSource,
  type ProductEvidenceVersionHistory,
  type ProductFinancialFact,
  type ProductFinancialProfile,
  type ProductFinancialPromotionResult,
  type ProductProcessingActivityItem,
  type ProductProcessingView,
  type ProductRecoveredProcessingResult,
} from './productApi';
import './product.css';

const { Dragger } = Upload;
const formatBytes = (value: number) =>
  value < 1024
    ? `${String(value)} B`
    : value < 1_048_576
      ? `${(value / 1024).toFixed(1)} KB`
      : '1 MB';
const typeName = (value: string) =>
  ({
    'application/pdf': 'PDF document',
    'image/png': 'PNG image',
    'image/jpeg': 'JPEG image',
    'text/plain': 'Plain text',
  })[value] ?? value;
const formatDate = (value: string) =>
  new Intl.DateTimeFormat(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(new Date(value));
const formatFinancialValue = (fact: ProductFinancialFact) =>
  new Intl.NumberFormat(undefined, {
    style: 'currency',
    currency: fact.currency,
  }).format(fact.value);

const financialSourceKey = (sourceFactId: string, documentVersionId: string) =>
  sourceFactId + '\u001f' + documentVersionId;

const processingActivityLabel = (state: ProductProcessingActivityItem['state']) =>
  ({
    Scheduled: 'Queued',
    Running: 'Extracting details',
    CancellationRequested: 'Stopping',
    Completed: 'Completed',
    Failed: "Couldn't complete",
    Cancelled: 'Cancelled',
  })[state];

const processingActivityColor = (state: ProductProcessingActivityItem['state']) =>
  state === 'Completed'
    ? 'success'
    : state === 'Failed'
      ? 'error'
      : state === 'Cancelled'
        ? 'default'
        : state === 'Running'
          ? 'processing'
          : 'warning';

export default function EvidenceExperience() {
  const { api, authorization } = useWorkspaceSession();
  const product = authorization?.area === 'product' ? authorization.context : null;
  const canUpload = product?.capabilities.includes('evidence.document.upload') ?? false;
  const canRead = product?.capabilities.includes('evidence.document.read') ?? false;
  const canProcess =
    product?.capabilities.includes('document-intelligence.document.process') ?? false;
  const canReadProcessingActivity =
    product?.capabilities.includes('job-management.job.read') ?? false;
  const canUpdateFinancial =
    product?.capabilities.includes('financial-profile.profile.update') ?? false;
  const canReadFinancial =
    product?.capabilities.includes('financial-profile.profile.read') ?? false;
  const [candidate, setCandidate] = useState<File | null>(null);
  const [operationId, setOperationId] = useState('');
  const [evidence, setEvidence] = useState<ProductEvidenceItem[]>([]);
  const [confirmation, setConfirmation] = useState<ProductEvidenceReceipt | null>(null);
  const [libraryBusy, setLibraryBusy] = useState(false);
  const [libraryError, setLibraryError] = useState('');
  const sourceVersionFocus = useRef(
    new URLSearchParams(window.location.search).get('version'),
  );
  const [focusedSource, setFocusedSource] = useState<ProductEvidenceSource | null>(null);
  const [sourceFocusBusy, setSourceFocusBusy] = useState(false);
  const [sourceFocusError, setSourceFocusError] = useState('');
  const [query, setQuery] = useState('');
  const [typeFilter, setTypeFilter] = useState('all');
  const [sortOrder, setSortOrder] = useState<'newest' | 'oldest' | 'name'>('newest');
  const [financialProfile, setFinancialProfile] = useState<ProductFinancialProfile | null>(null);
  const [financialLinkError, setFinancialLinkError] = useState('');
  const [selectedEvidence, setSelectedEvidence] = useState<ProductEvidenceItem | null>(null);
  const [versionHistory, setVersionHistory] = useState<ProductEvidenceVersionHistory | null>(null);
  const [versionHistoryBusy, setVersionHistoryBusy] = useState(false);
  const [versionHistoryError, setVersionHistoryError] = useState('');
  const [processingEvidence, setProcessingEvidence] = useState<ProductEvidenceItem | null>(null);
  const [processing, setProcessing] = useState<ProductProcessingView | null>(null);
  const [processingActivity, setProcessingActivity] = useState<ProductProcessingActivityItem[]>([]);
  const [processingActivityBusy, setProcessingActivityBusy] = useState(false);
  const [processingActivityError, setProcessingActivityError] = useState('');
  const [processingActivityLoaded, setProcessingActivityLoaded] = useState(false);
  const [recoveredResultJob, setRecoveredResultJob] =
    useState<ProductProcessingActivityItem | null>(null);
  const [recoveredResult, setRecoveredResult] =
    useState<ProductRecoveredProcessingResult | null>(null);
  const [recoveredResultBusy, setRecoveredResultBusy] = useState(false);
  const [recoveredResultError, setRecoveredResultError] = useState('');
  const [recoveredSelectedSourceFactIds, setRecoveredSelectedSourceFactIds] = useState<string[]>([]);
  const [recoveredPromotionBusy, setRecoveredPromotionBusy] = useState(false);
  const [recoveredPromotionError, setRecoveredPromotionError] = useState('');
  const [recoveredPromotionResult, setRecoveredPromotionResult] =
    useState<ProductFinancialPromotionResult | null>(null);
  const [processingBusy, setProcessingBusy] = useState(false);
  const [processingError, setProcessingError] = useState('');
  const [selectedSourceFactIds, setSelectedSourceFactIds] = useState<string[]>([]);
  const [promotionBusy, setPromotionBusy] = useState(false);
  const [promotionError, setPromotionError] = useState('');
  const [promotionResult, setPromotionResult] =
    useState<ProductFinancialPromotionResult | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const loadLibrary = useCallback(
    async (signal?: AbortSignal) => {
      if (!api || !canRead) {
        setEvidence([]);
        return;
      }
      setLibraryBusy(true);
      setLibraryError('');
      try {
        const library = await listEvidence(api, signal);
        setEvidence(library.items);
      } catch (failure) {
        if (!signal?.aborted) {
          setLibraryError(
            failure instanceof AccessApiError && failure.status === 403
              ? "You don't have access to these documents. Contact your organization administrator."
              : "We couldn't load your documents. Try again.",
          );
        }
      } finally {
        if (!signal?.aborted) setLibraryBusy(false);
      }
    },
    [api, canRead],
  );
  const loadProcessingActivity = useCallback(
    async (signal?: AbortSignal) => {
      if (!api || !canReadProcessingActivity) {
        setProcessingActivity([]);
        setProcessingActivityLoaded(false);
        return;
      }
      setProcessingActivityBusy(true);
      setProcessingActivityError('');
      try {
        const activity = await getProcessingActivity(api, signal);
        setProcessingActivity(activity.items);
        setProcessingActivityLoaded(true);
      } catch (failure) {
        if (!signal?.aborted) {
          setProcessingActivityLoaded(false);
          setProcessingActivityError(
            failure instanceof AccessApiError && failure.status === 403
              ? "Processing activity isn't included in your current access."
              : "We couldn't refresh document processing activity.",
          );
        }
      } finally {
        if (!signal?.aborted) setProcessingActivityBusy(false);
      }
    },
    [api, canReadProcessingActivity],
  );

  useEffect(() => {
    const controller = new AbortController();
    const start = window.setTimeout(() => {
      void loadLibrary(controller.signal);
    }, 0);
    return () => {
      window.clearTimeout(start);
      controller.abort();
    };
  }, [loadLibrary]);
  useEffect(() => {
    const controller = new AbortController();
    const start = window.setTimeout(() => {
      void loadProcessingActivity(controller.signal);
    }, 0);
    return () => {
      window.clearTimeout(start);
      controller.abort();
    };
  }, [loadProcessingActivity]);
  useEffect(() => {
    if (!api || !canReadFinancial) {
      setFinancialProfile(null);
      setFinancialLinkError('');
      return;
    }
    const controller = new AbortController();
    setFinancialLinkError('');
    void getFinancialProfile(api, controller.signal)
      .then(setFinancialProfile)
      .catch((failure) => {
        if (!controller.signal.aborted) {
          setFinancialProfile(null);
          setFinancialLinkError(
            failure instanceof AccessApiError && failure.status === 403
              ? "Linked financial information isn't included in your current access."
              : "We couldn't refresh linked financial information.",
          );
        }
      });
    return () => controller.abort();
  }, [api, canReadFinancial]);
  useEffect(() => {
    if (!selectedEvidence || !api || !canRead) {
      setVersionHistory(null);
      setVersionHistoryError('');
      setVersionHistoryBusy(false);
      return;
    }
    const controller = new AbortController();
    setVersionHistory(null);
    setVersionHistoryError('');
    setVersionHistoryBusy(true);
    void getEvidenceVersionHistory(api, selectedEvidence.documentId, controller.signal)
      .then(setVersionHistory)
      .catch((failure) => {
        if (controller.signal.aborted) return;
        setVersionHistory(null);
        setVersionHistoryError(
          failure instanceof AccessApiError && failure.status === 403
            ? "You no longer have access to this document history."
            : failure instanceof AccessApiError && failure.status === 404
              ? "This document history isn't available in your current document access."
              : "We couldn't refresh this document's version history.",
        );
      })
      .finally(() => {
        if (!controller.signal.aborted) setVersionHistoryBusy(false);
      });
    return () => controller.abort();
  }, [api, canRead, selectedEvidence]);

  useEffect(() => {
    const version = sourceVersionFocus.current;
    if (!version || !api || !canRead) return;
    sourceVersionFocus.current = null;
    window.history.replaceState(null, '', '/evidence');
    const controller = new AbortController();
    setSourceFocusBusy(true);
    setSourceFocusError('');
    void getEvidenceSource(api, version, controller.signal)
      .then(setFocusedSource)
      .catch((failure) => {
        if (controller.signal.aborted) return;
        setSourceFocusError(
          failure instanceof AccessApiError && failure.status === 403
            ? "You no longer have access to this source document."
            : failure instanceof AccessApiError && failure.status === 404
              ? "The source document version isn't available in your current document access."
              : "We couldn't open the source document. Try again.",
        );
      })
      .finally(() => {
        if (!controller.signal.aborted) setSourceFocusBusy(false);
      });
    return () => controller.abort();
  }, [api, canRead]);
  const linkedFactsByVersion = useMemo(() => {
    const grouped = new Map<string, ProductFinancialFact[]>();
    for (const fact of financialProfile?.facts ?? []) {
      if (fact.factType !== 'INCOME' && fact.factType !== 'EXPENSE' && fact.factType !== 'LOAN' && fact.factType !== 'ASSET' && fact.factType !== 'INVESTMENT' && fact.factType !== 'INSURANCE' && fact.factType !== 'TAX') continue;
      const current = grouped.get(fact.source.documentVersion) ?? [];
      current.push(fact);
      grouped.set(fact.source.documentVersion, current);
    }
    return grouped;
  }, [financialProfile]);

  const trustedFactsBySourceKey = useMemo(() => {
    const mapped = new Map<string, ProductFinancialFact>();
    for (const fact of financialProfile?.facts ?? []) {
      if (fact.factType !== 'INCOME' && fact.factType !== 'EXPENSE' && fact.factType !== 'LOAN' && fact.factType !== 'ASSET' && fact.factType !== 'INVESTMENT' && fact.factType !== 'INSURANCE' && fact.factType !== 'TAX') continue;
      if (!fact.source.technicalSourceFactId || !fact.source.documentVersion) continue;
      mapped.set(
        financialSourceKey(fact.source.technicalSourceFactId, fact.source.documentVersion),
        fact,
      );
    }
    return mapped;
  }, [financialProfile]);

  useEffect(() => {
    if (!recoveredResult || trustedFactsBySourceKey.size === 0) return;
    setRecoveredSelectedSourceFactIds((current) =>
      current.filter(
        (sourceFactId) =>
          !trustedFactsBySourceKey.has(
            financialSourceKey(sourceFactId, recoveredResult.documentVersionId),
          ),
      ),
    );
  }, [recoveredResult, trustedFactsBySourceKey]);

  const visibleEvidence = useMemo(() => {
    const term = query.trim().toLocaleLowerCase();
    const filtered = evidence.filter(
      (item) =>
        (typeFilter === 'all' || item.contentType === typeFilter) &&
        (!term ||
          item.originalFileName.toLocaleLowerCase().includes(term) ||
          item.documentVersionId.toLocaleLowerCase().includes(term)),
    );
    return [...filtered].sort((left, right) => {
      if (sortOrder === 'name')
        return left.originalFileName.localeCompare(right.originalFileName, undefined, {
          sensitivity: 'base',
        });
      const compared = left.createdAt.localeCompare(right.createdAt);
      return sortOrder === 'oldest' ? compared : -compared;
    });
  }, [evidence, query, sortOrder, typeFilter]);

  const evidenceByVersion = useMemo(
    () => new Map(evidence.map((item) => [item.documentVersionId, item])),
    [evidence],
  );

  const selectedLinkedFacts = useMemo(
    () =>
      selectedEvidence
        ? [...(linkedFactsByVersion.get(selectedEvidence.documentVersionId) ?? [])].sort((left, right) =>
            left.label.localeCompare(right.label),
          )
        : [],
    [linkedFactsByVersion, selectedEvidence],
  );
  const openDocumentVersion = async (documentVersionId: string) => {
    if (!api || !canRead || versionHistoryBusy) return;
    setVersionHistoryBusy(true);
    setVersionHistoryError('');
    try {
      const source = await getEvidenceSource(api, documentVersionId);
      setSelectedEvidence(null);
      setVersionHistory(null);
      setFocusedSource(source);
    } catch (failure) {
      setVersionHistoryError(
        failure instanceof AccessApiError && failure.status === 403
          ? "You no longer have access to this document version."
          : failure instanceof AccessApiError && failure.status === 404
            ? "This document version isn't available in your current document access."
            : "We couldn't open this document version.",
      );
    } finally {
      setVersionHistoryBusy(false);
    }
  };

  const issue = useMemo(() => {
    if (!candidate) return '';
    if (!evidenceTypes.includes(candidate.type))
      return 'Choose a PDF, PNG, JPEG or plain-text file.';
    if (candidate.size < 1 || candidate.size > maximumEvidenceBytes)
      return 'Choose a non-empty file no larger than 1 MB.';
    if (
      candidate.name.length > 180 ||
      candidate.name.includes('/') ||
      candidate.name.includes('\\')
    )
      return 'Choose a file with a shorter, safe file name.';
    return '';
  }, [candidate]);
  const select: UploadProps['beforeUpload'] = (file) => {
    setCandidate(file);
    setOperationId(crypto.randomUUID().replaceAll('-', ''));
    setError('');
    return false;
  };
  const close = () => {
    if (busy) return;
    setCandidate(null);
    setOperationId('');
    setError('');
  };
  const upload = async () => {
    if (!candidate || issue || !api || busy) return;
    setBusy(true);
    setError('');
    try {
      const receipt = await submitEvidence(api, operationId, candidate);
      setConfirmation(receipt);
      if (receipt.documentVersionId) await loadLibrary();
      window.setTimeout(close, 250);
    } catch (failure) {
      setError(
        failure instanceof AccessApiError && failure.status === 403
          ? "You don't have permission to upload documents. Contact your organization administrator."
          : failure instanceof AccessApiError && failure.status === 409
            ? "We couldn't confirm this upload because the same request was used for different content. Choose the file again."
            : "We couldn't confirm the upload. Try again to check the same request.",
      );
    } finally {
      setBusy(false);
    }
  };
  const openRecoveredProcessingResult = async (item: ProductProcessingActivityItem) => {
    if (!api || !canReadProcessingActivity || !canRead || item.state !== 'Completed') return;
    setRecoveredResultJob(item);
    setRecoveredResult(null);
    setRecoveredResultError('');
    setRecoveredSelectedSourceFactIds([]);
    setRecoveredPromotionError('');
    setRecoveredPromotionResult(null);
    setRecoveredResultBusy(true);
    try {
      const result = await getRecoveredProcessingResult(api, item.jobId);
      if (result.jobId !== item.jobId || result.documentVersionId !== item.documentVersionId)
        throw new Error('Recovered processing result identity mismatch.');
      setRecoveredResult(result);
    } catch (failure) {
      setRecoveredResultError(
        failure instanceof AccessApiError && failure.status === 403
          ? "You no longer have access to this processing result."
          : failure instanceof AccessApiError && failure.status === 404
            ? "This processing result isn't available in your current access."
            : failure instanceof AccessApiError && failure.status === 409
              ? "This processing result isn't ready yet."
              : "We couldn't open this processing result.",
      );
    } finally {
      setRecoveredResultBusy(false);
    }
  };

  const closeRecoveredProcessingResult = () => {
    if (recoveredResultBusy || recoveredPromotionBusy) return;
    setRecoveredResultJob(null);
    setRecoveredResult(null);
    setRecoveredResultError('');
    setRecoveredSelectedSourceFactIds([]);
    setRecoveredPromotionError('');
    setRecoveredPromotionResult(null);
  };

  const toggleRecoveredSourceFact = (sourceFactId: string, checked: boolean) => {
    setRecoveredPromotionError('');
    setRecoveredPromotionResult(null);
    setRecoveredSelectedSourceFactIds((current) =>
      checked ? [...current, sourceFactId] : current.filter((id) => id !== sourceFactId),
    );
  };

  const addRecoveredReviewedFacts = async () => {
    if (
      !api ||
      !recoveredResultJob ||
      !recoveredResult ||
      recoveredResult.state !== 'Completed' ||
      recoveredSelectedSourceFactIds.length === 0 ||
      recoveredPromotionBusy ||
      !canUpdateFinancial ||
      !canReadFinancial ||
      financialProfile === null ||
      Boolean(financialLinkError)
    )
      return;

    setRecoveredPromotionBusy(true);
    setRecoveredPromotionError('');
    setRecoveredPromotionResult(null);
    try {
      const result = await addProcessingFactsToFinancialProfile(
        api,
        recoveredResultJob.jobId,
        crypto.randomUUID().replaceAll('-', ''),
        recoveredSelectedSourceFactIds,
      );
      setRecoveredPromotionResult(result);
      setFinancialProfile(result.profile);
    } catch (failure) {
      setRecoveredPromotionError(
        failure instanceof AccessApiError && failure.status === 403
          ? 'Your access to review or update financial information changed. Ask your organization administrator to review the current Customer access package.'
          : failure instanceof AccessApiError && failure.status === 409
            ? 'Your financial information or this completed review changed before it could be confirmed. Close this view, refresh, and review the result again.'
            : "We couldn't add the selected information. Your financial information has not been confirmed as changed.",
      );
    } finally {
      setRecoveredPromotionBusy(false);
    }
  };

  const closeProcessing = () => {
    if (processingBusy || promotionBusy) return;
    setProcessingEvidence(null);
    setProcessing(null);
    setProcessingError('');
    setSelectedSourceFactIds([]);
    setPromotionError('');
    setPromotionResult(null);
  };
  const processDocument = async () => {
    if (!api || !processingEvidence || processingBusy || !canProcess) return;
    setProcessingBusy(true);
    setProcessingError('');
    const controller = new AbortController();
    try {
      let view = await scheduleDocumentProcessing(
        api,
        processingEvidence.documentVersionId,
        crypto.randomUUID().replaceAll('-', ''),
        controller.signal,
      );
      setProcessing(view);
      for (
        let attempt = 0;
        attempt < 30 && !['Completed', 'Failed', 'Cancelled'].includes(view.job.state);
        attempt += 1
      ) {
        await new Promise((resolve) => window.setTimeout(resolve, 400));
        view = await getDocumentProcessing(api, view.job.jobId, controller.signal);
        setProcessing(view);
      }
      if (!['Completed', 'Failed', 'Cancelled'].includes(view.job.state))
        setProcessingError(
          "We're still extracting details. Close this view and reopen the document to check again.",
        );
    } catch (failure) {
      setProcessingError(
        failure instanceof AccessApiError && failure.status === 403
          ? 'You no longer have access to extract details from this document.'
          : failure instanceof AccessApiError && failure.status === 422
            ? 'Extracting details is currently available only for structured text files.'
            : "We couldn't extract details from this document. Your financial information has not changed.",
      );
    } finally {
      controller.abort();
      setProcessingBusy(false);
      void loadProcessingActivity();
    }
  };
  const toggleSourceFact = (sourceFactId: string, checked: boolean) => {
    setPromotionError('');
    setPromotionResult(null);
    setSelectedSourceFactIds((current) =>
      checked ? [...current, sourceFactId] : current.filter((id) => id !== sourceFactId),
    );
  };
  const addReviewedFacts = async () => {
    if (
      !api ||
      !processing?.job ||
      processing.job.state !== 'Completed' ||
      selectedSourceFactIds.length === 0 ||
      promotionBusy ||
      !canUpdateFinancial
    )
      return;
    setPromotionBusy(true);
    setPromotionError('');
    setPromotionResult(null);
    try {
      const result = await addProcessingFactsToFinancialProfile(
        api,
        processing.job.jobId,
        crypto.randomUUID().replaceAll('-', ''),
        selectedSourceFactIds,
      );
      setPromotionResult(result);
      setFinancialProfile(result.profile);
    } catch (failure) {
      setPromotionError(
        failure instanceof AccessApiError && failure.status === 403
          ? 'Your access to update financial information changed. Ask your organization administrator to review the current Customer access package.'
          : failure instanceof AccessApiError && failure.status === 409
            ? 'Your financial information or this review changed before it could be confirmed. Close this view, refresh, and review the document again.'
            : "We couldn't add the selected information. Your financial information has not been confirmed as changed.",
      );
    } finally {
      setPromotionBusy(false);
    }
  };

  return (
    <main className="product-page evidence-page">
      <header className="product-heading">
        <div>
          <p className="workspace-eyebrow">DOCUMENTS</p>
          <h1>Keep your financial documents together.</h1>
          <p>
            Add documents to {product?.customerName ?? 'your workspace'}, review supported details,
            and see which trusted financial items use each exact document version.
          </p>
        </div>
        <Tag className="product-environment">TEST ENVIRONMENT · TEMPORARY DATA</Tag>
      </header>

      <section className="product-intake-grid" aria-labelledby="upload-heading">
        <div className="product-panel product-upload-panel">
          <div className="product-section-title">
            <span className="product-icon">
              <WorkspaceIcon name="evidence" size={20} />
            </span>
            <div>
              <h2 id="upload-heading">Add document</h2>
              <p>PDF, PNG, JPEG or text · up to 1 MB</p>
            </div>
          </div>
          {!canUpload && (
            <Alert
              showIcon
              type="warning"
              title="Document upload is not included in your current access"
              description="Contact your organization administrator if you need access to upload documents."
            />
          )}
          <Dragger
            accept={evidenceTypes.join(',')}
            multiple={false}
            showUploadList={false}
            beforeUpload={select}
            disabled={!canUpload}
            className="product-drop-zone"
          >
            <span className="product-drop-icon">
              <WorkspaceIcon name="evidence" size={27} />
            </span>
            <p className="ant-upload-text">Drop a document here</p>
            <p className="ant-upload-hint">or choose a file to review before uploading</p>
          </Dragger>
          <div className="product-trust-row">
            <span>
              <WorkspaceIcon name="shield" size={16} /> Linked to your customer account
            </span>
            <span>
              <WorkspaceIcon name="check" size={16} /> Access checked when you upload
            </span>
          </div>
        </div>
        <aside className="product-panel product-explainer" aria-labelledby="evidence-result-title">
          <p className="workspace-eyebrow">WHAT HAPPENS NEXT</p>
          <h2 id="evidence-result-title">Your document stays connected to its details.</h2>
          <ol>
            <li>
              <span>01</span>
              <div>
                <strong>Check file</strong>
                <p>Monergy checks the file type, size and integrity.</p>
              </div>
            </li>
            <li>
              <span>02</span>
              <div>
                <strong>Save document</strong>
                <p>Monergy records a document version you can return to.</p>
              </div>
            </li>
            <li>
              <span>03</span>
              <div>
                <strong>Ready to review</strong>
                <p>You can open the document details and extract supported information.</p>
              </div>
            </li>
          </ol>
          <p className="product-limit-note">
            Test environment · Temporary data. These documents may be cleared when this test
            environment is reset.
          </p>
        </aside>
      </section>

      <section className="product-recent" aria-labelledby="evidence-library-title">
        <div className="product-section-heading">
          <div>
            <p className="workspace-eyebrow">YOUR DOCUMENTS</p>
            <h2 id="evidence-library-title">Your documents</h2>
          </div>
          <span>
            {evidence.length ? `${String(evidence.length)} available` : 'No documents yet'}
          </span>
        </div>
        {sourceFocusBusy && (
          <Alert
            showIcon
            type="info"
            title="Opening source document…"
            className="product-library-alert"
          />
        )}
        {sourceFocusError && (
          <Alert
            showIcon
            type="warning"
            title={sourceFocusError}
            className="product-library-alert"
          />
        )}
        {confirmation && (
          <Alert
            showIcon
            closable={{
              onClose: () => {
                setConfirmation(null);
              },
            }}
            type="success"
            title="Your document was uploaded."
            description={`${confirmation.originalFileName} · Version ${String(confirmation.version)}`}
            className="product-library-alert"
          />
        )}
        {!canRead && (
          <Alert
            showIcon
            type="warning"
            title="You don't have access to these documents"
            description="Contact your organization administrator if you need access to view existing documents."
            className="product-library-alert"
          />
        )}
        {canRead && (
          <div className="product-library-controls">
            <Input
              allowClear
              aria-label="Find documents"
              placeholder="Find by file name"
              value={query}
              onChange={(event) => {
                setQuery(event.target.value);
              }}
            />
            <Select
              id="evidence-type-filter"
              aria-label="Filter document type"
              value={typeFilter}
              onChange={setTypeFilter}
              options={[
                { value: 'all', label: 'All file types' },
                ...evidenceTypes.map((value) => ({ value, label: typeName(value) })),
              ]}
            />
            <Select
              id="evidence-sort-order"
              aria-label="Sort documents"
              value={sortOrder}
              onChange={setSortOrder}
              options={[
                { value: 'newest', label: 'Newest first' },
                { value: 'oldest', label: 'Oldest first' },
                { value: 'name', label: 'File name A–Z' },
              ]}
            />
            <Button
              loading={libraryBusy}
              onClick={() => {
                void loadLibrary();
              }}
            >
              Refresh
            </Button>
          </div>
        )}
        {libraryError && (
          <Alert
            showIcon
            type="error"
            title={libraryError}
            action={
              <Button
                size="small"
                onClick={() => {
                  void loadLibrary();
                }}
              >
                Retry
              </Button>
            }
            className="product-library-alert"
          />
        )}
        {libraryBusy && evidence.length === 0 ? (
          <Skeleton active paragraph={{ rows: 3 }} className="product-library-loading" />
        ) : canRead && evidence.length === 0 ? (
          <div className="product-empty">
            <WorkspaceIcon name="evidence" size={24} />
            <div>
              <strong>No documents yet.</strong>
              <p>Add a document above to get started.</p>
            </div>
          </div>
        ) : canRead && visibleEvidence.length === 0 ? (
          <div className="product-empty">
            <WorkspaceIcon name="search" size={24} />
            <div>
              <strong>No documents match these filters.</strong>
              <p>Clear the search or choose another file type.</p>
            </div>
          </div>
        ) : (
          <div className="product-receipts">
            {visibleEvidence.map((item) => (
              <article key={item.documentVersionId} className="product-receipt">
                <button
                  type="button"
                  className="product-receipt-action"
                  aria-label={`View document details for ${item.originalFileName}`}
                  onClick={() => {
                    setSelectedEvidence(item);
                  }}
                >
                  <span className="product-icon">
                    <WorkspaceIcon name="check" size={18} />
                  </span>
                  <span className="product-receipt-copy">
                    <strong>{item.originalFileName}</strong>
                    <span>
                      {typeName(item.contentType)} · {formatBytes(item.contentLength)} · Version{' '}
                      {item.version}
                    </span>
                  </span>
                  <span className="product-receipt-meta">
                    <Tag color="success">UPLOADED</Tag>
                    {canReadFinancial && financialProfile && (
                      <small>
                        {(linkedFactsByVersion.get(item.documentVersionId) ?? []).length}{' '}
                        {(linkedFactsByVersion.get(item.documentVersionId) ?? []).length === 1
                          ? 'trusted item'
                          : 'trusted items'}
                      </small>
                    )}
                    <small>{formatDate(item.createdAt)}</small>
                  </span>
                  <WorkspaceIcon name="arrow" size={17} />
                </button>
              </article>
            ))}
          </div>
        )}
      </section>

      <section className="product-recent product-processing-activity" aria-labelledby="processing-activity-title">
        <div className="product-section-heading">
          <div>
            <p className="workspace-eyebrow">PROCESSING ACTIVITY</p>
            <h2 id="processing-activity-title">Recent document processing</h2>
          </div>
          <Button
            loading={processingActivityBusy}
            disabled={!canReadProcessingActivity}
            onClick={() => {
              void loadProcessingActivity();
            }}
          >
            Refresh
          </Button>
        </div>
        {!canReadProcessingActivity ? (
          <Alert
            showIcon
            type="warning"
            title="Processing activity isn't included in your current access"
            description="Your organization administrator can review the current Customer access package."
            className="product-library-alert"
          />
        ) : processingActivityError ? (
          <Alert
            showIcon
            type="warning"
            title={processingActivityError}
            className="product-library-alert"
          />
        ) : processingActivityBusy && !processingActivityLoaded ? (
          <Skeleton active paragraph={{ rows: 2 }} className="product-library-loading" />
        ) : processingActivityLoaded && processingActivity.length === 0 ? (
          <div className="product-empty">
            <WorkspaceIcon name="changes" size={24} />
            <div>
              <strong>No document processing activity yet.</strong>
              <p>Processing appears here after you extract details from a supported document.</p>
            </div>
          </div>
        ) : (
          <div className="product-processing-activity-list">
            {processingActivity.map((item) => {
              const document = evidenceByVersion.get(item.documentVersionId);
              return (
                <article className="product-processing-activity-item" key={item.jobId}>
                  <span className="product-processing-activity-icon">
                    <WorkspaceIcon
                      name={item.state === 'Completed' ? 'check' : 'changes'}
                      size={18}
                    />
                  </span>
                  <div>
                    <strong>{document?.originalFileName ?? 'Document processing'}</strong>
                    <span>
                      Updated {formatDate(item.updatedAt)}
                      {item.attempt > 0 ? ' · Attempt ' + String(item.attempt) : ''}
                    </span>
                  </div>
                  <Tag color={processingActivityColor(item.state)}>
                    {processingActivityLabel(item.state)}
                  </Tag>
                  <div className="product-processing-activity-actions">
                    {item.state === 'Completed' && canReadProcessingActivity && canRead && (
                      <Button
                        type="link"
                        onClick={() => {
                          void openRecoveredProcessingResult(item);
                        }}
                      >
                        View result
                      </Button>
                    )}
                    {document && canRead && (
                      <Button
                        type="link"
                        onClick={() => {
                          setSelectedEvidence(document);
                        }}
                      >
                        Open document
                      </Button>
                    )}
                  </div>
                </article>
              );
            })}
          </div>
        )}
        <p className="product-processing-activity-note">
          This is read-only processing status. Retry, cancellation, reconciliation and support
          controls are not part of the Customer workspace.
        </p>
      </section>

      <Drawer
        title="Processing result"
        aria-label="Processing result"
        open={Boolean(recoveredResultJob)}
        onClose={closeRecoveredProcessingResult}
        destroyOnHidden
        size="default"
        closable={!recoveredResultBusy && !recoveredPromotionBusy}
        mask={{ closable: !recoveredResultBusy && !recoveredPromotionBusy }}
      >
        {recoveredResultJob && (
          <div className="product-processing-review product-recovered-processing">
            <div className="product-processing-hero">
              <span className="product-review-icon">
                <WorkspaceIcon name="check" size={25} />
              </span>
              <div>
                <Tag color="success">COMPLETED</Tag>
                <h3>
                  {evidenceByVersion.get(recoveredResultJob.documentVersionId)?.originalFileName ??
                    'Document processing result'}
                </h3>
                <p>
                  {canUpdateFinancial &&
                  canReadFinancial &&
                  financialProfile !== null &&
                  !financialLinkError
                    ? 'Resume review from this completed processing result'
                    : 'Read-only recovered processing result'}
                </p>
              </div>
            </div>

            {recoveredResultBusy ? (
              <Skeleton active paragraph={{ rows: 5 }} />
            ) : recoveredResultError ? (
              <Alert showIcon type="warning" title={recoveredResultError} />
            ) : recoveredResult ? (
              <>
                <div className="product-recovered-processing-summary">
                  <span>
                    Updated <strong>{formatDate(recoveredResult.updatedAt)}</strong>
                  </span>
                  <span>
                    Extracted items <strong>{recoveredResult.facts.length}</strong>
                  </span>
                </div>
                <section className="product-facts" aria-labelledby="recovered-source-facts-title">
                  <div className="product-section-heading">
                    <div>
                      <p className="workspace-eyebrow">EXTRACTED INFORMATION</p>
                      <h2 id="recovered-source-facts-title">Information found in this processing run</h2>
                    </div>
                    <span>{recoveredResult.facts.length} items</span>
                  </div>
                  <div className="product-fact-list">
                    {recoveredResult.facts.map((fact) => {
                      const trusted = trustedFactsBySourceKey.get(
                        financialSourceKey(fact.sourceFactId, recoveredResult.documentVersionId),
                      );
                      const selected = recoveredSelectedSourceFactIds.includes(fact.sourceFactId);
                      const reviewReady =
                        canUpdateFinancial &&
                        canReadFinancial &&
                        financialProfile !== null &&
                        !financialLinkError;
                      return (
                        <article
                          className={
                            trusted
                              ? 'product-fact-card trusted'
                              : selected
                                ? 'product-fact-card selected'
                                : 'product-fact-card'
                          }
                          key={fact.sourceFactId}
                        >
                          {reviewReady && !trusted && (
                            <Checkbox
                              checked={selected}
                              disabled={recoveredPromotionBusy || Boolean(recoveredPromotionResult)}
                              onChange={(event) =>
                                toggleRecoveredSourceFact(fact.sourceFactId, event.target.checked)
                              }
                              aria-label={'Add ' + fact.label + ' to financial information'}
                            />
                          )}
                          <div>
                            <span className="product-fact-tags">
                              <Tag>{fact.factType === 'INCOME' ? 'Income' : fact.factType === 'LOAN' ? 'Loan information' : fact.factType === 'ASSET' ? 'Asset source figure' : fact.factType === 'INVESTMENT' ? 'Investment source figure' : fact.factType === 'INSURANCE' ? 'Insurance source figure' : fact.factType === 'TAX' ? 'Tax source figure' : 'Expense'}</Tag>
                              {trusted && <Tag color="success">ALREADY TRUSTED</Tag>}
                            </span>
                            <strong>{fact.label}</strong>
                            <span>
                              {fact.sourceLocation} · {fact.effectiveDate}
                            </span>
                            {trusted && (
                              <a
                                className="product-fact-trust-link"
                                href={'/finances?fact=' + encodeURIComponent(trusted.financialFactId)}
                              >
                                View trusted Financial information
                              </a>
                            )}
                          </div>
                          <div className="product-fact-value">
                            <strong>
                              {new Intl.NumberFormat(undefined, {
                                style: 'currency',
                                currency: fact.currency,
                              }).format(fact.candidateValue)}
                            </strong>
                            <span>{Math.round(fact.confidence * 100)}% confidence</span>
                          </div>
                        </article>
                      );
                    })}
                  </div>
                </section>
                {canUpdateFinancial && canReadFinancial ? (
                  financialLinkError ? (
                    <Alert
                      showIcon
                      type="warning"
                      title="Trusted Financial information could not be reconciled"
                      description="This recovered result remains read-only until the current Financial Profile can be refreshed. Monergy will not guess which extracted items are already trusted."
                    />
                  ) : financialProfile === null ? (
                    <Alert
                      showIcon
                      type="info"
                      title="Checking trusted Financial information"
                      description="Selection remains unavailable until Monergy can determine which extracted items are already represented."
                    />
                  ) : recoveredResult.facts.every((fact) =>
                      trustedFactsBySourceKey.has(
                        financialSourceKey(fact.sourceFactId, recoveredResult.documentVersionId),
                      ),
                    ) ? (
                    <>
                      {recoveredPromotionResult && (
                        <Alert
                          showIcon
                          type="success"
                          title={
                            String(recoveredPromotionResult.confirmed) +
                            (recoveredPromotionResult.confirmed === 1
                              ? ' item added to Financial information'
                              : ' items added to Financial information')
                          }
                        />
                      )}
                      <Alert
                        showIcon
                        type="success"
                        title="All supported items from this run are already trusted"
                        description="Each extracted item in this completed run is already represented in current Financial information. Use the item links above to inspect the trusted facts."
                      />
                    </>
                  ) : (
                    <>
                      <Alert
                        showIcon
                        type="info"
                        title="Review before adding"
                        description="Items already represented in trusted Financial information are marked and cannot be selected again. Select only the remaining candidate items you have reviewed."
                      />
                      {recoveredPromotionError && (
                        <Alert showIcon type="error" title={recoveredPromotionError} />
                      )}
                      {recoveredPromotionResult && (
                        <Alert
                          showIcon
                          type="success"
                          title={
                            String(recoveredPromotionResult.confirmed) +
                            (recoveredPromotionResult.confirmed === 1
                              ? ' item added to Financial information'
                              : ' items added to Financial information')
                          }
                        />
                      )}
                    </>
                  )
                ) : (
                  <Alert
                    showIcon
                    type="info"
                    title="This recovered view is read-only"
                    description="You can review the candidate information produced by this completed run, but adding it to trusted Financial information is not included in your current access."
                  />
                )}
                <div className="product-drawer-actions">
                  {canUpdateFinancial &&
                    canReadFinancial &&
                    financialProfile !== null &&
                    !financialLinkError &&
                    recoveredResult.facts.some(
                      (fact) =>
                        !trustedFactsBySourceKey.has(
                          financialSourceKey(fact.sourceFactId, recoveredResult.documentVersionId),
                        ),
                    ) && (
                    <Button
                      type="primary"
                      loading={recoveredPromotionBusy}
                      disabled={
                        recoveredSelectedSourceFactIds.length === 0 ||
                        Boolean(recoveredPromotionResult)
                      }
                      onClick={() => {
                        void addRecoveredReviewedFacts();
                      }}
                    >
                      Add selected information
                    </Button>
                  )}
                  {evidenceByVersion.has(recoveredResult.documentVersionId) && (
                    <Button
                      disabled={recoveredPromotionBusy}
                      onClick={() => {
                        const document = evidenceByVersion.get(recoveredResult.documentVersionId);
                        if (!document) return;
                        closeRecoveredProcessingResult();
                        setSelectedEvidence(document);
                      }}
                    >
                      Open document
                    </Button>
                  )}
                </div>
                <details className="product-review-note">
                  <summary>Technical details</summary>
                  <dl>
                    <div>
                      <dt>Job ID</dt>
                      <dd className="product-identifier">{recoveredResult.jobId}</dd>
                    </div>
                    <div>
                      <dt>Version ID</dt>
                      <dd className="product-identifier">{recoveredResult.documentVersionId}</dd>
                    </div>
                  </dl>
                </details>
              </>
            ) : null}
          </div>
        )}
      </Drawer>

      <Drawer
        title="Source document details"
        aria-label="Source document details"
        open={Boolean(focusedSource)}
        onClose={() => {
          setFocusedSource(null);
        }}
        destroyOnHidden
        size="default"
      >
        {focusedSource && (
          <div className="product-review product-provenance">
            <span className="product-review-icon">
              <WorkspaceIcon name="evidence" size={25} />
            </span>
            <Tag color="success">SOURCE DOCUMENT</Tag>
            <h3>{focusedSource.originalFileName}</h3>
            <p>{typeName(focusedSource.contentType)}</p>
            <dl>
              <div>
                <dt>Customer</dt>
                <dd>{product?.customerName}</dd>
              </div>
              <div>
                <dt>Version</dt>
                <dd>{focusedSource.version}</dd>
              </div>
              <div>
                <dt>Received</dt>
                <dd>{formatDate(focusedSource.receivedAt)}</dd>
              </div>
              <div>
                <dt>Source</dt>
                <dd>{focusedSource.sourceName}</dd>
              </div>
            </dl>
            <details className="product-review-note">
              <summary>Technical details</summary>
              <dl>
                <div>
                  <dt>Document ID</dt>
                  <dd className="product-identifier">{focusedSource.documentId}</dd>
                </div>
                <div>
                  <dt>Version ID</dt>
                  <dd className="product-identifier">{focusedSource.documentVersionId}</dd>
                </div>
                <div>
                  <dt>Evidence ID</dt>
                  <dd className="product-identifier">{focusedSource.evidenceId}</dd>
                </div>
                <div>
                  <dt>SHA-256</dt>
                  <dd className="product-hash">{focusedSource.contentSha256}</dd>
                </div>
              </dl>
            </details>
          </div>
        )}
      </Drawer>

      <Drawer
        title="Document details"
        aria-label="Document details"
        open={Boolean(selectedEvidence)}
        onClose={() => {
          setSelectedEvidence(null);
          setVersionHistory(null);
          setVersionHistoryError('');
        }}
        destroyOnHidden
        size="default"
      >
        {selectedEvidence && (
          <div className="product-review product-provenance">
            <span className="product-review-icon">
              <WorkspaceIcon name="check" size={25} />
            </span>
            <Tag color="success">UPLOADED</Tag>
            <h3>{selectedEvidence.originalFileName}</h3>
            <p>
              {typeName(selectedEvidence.contentType)} ·{' '}
              {formatBytes(selectedEvidence.contentLength)}
            </p>
            <dl>
              <div>
                <dt>Customer</dt>
                <dd>{product?.customerName}</dd>
              </div>
              <div>
                <dt>Version</dt>
                <dd>{selectedEvidence.version}</dd>
              </div>
              <div>
                <dt>Uploaded</dt>
                <dd>{formatDate(selectedEvidence.createdAt)}</dd>
              </div>
              <div>
                <dt>Received</dt>
                <dd>{formatDate(selectedEvidence.receivedAt)}</dd>
              </div>
              <div>
                <dt>Source</dt>
                <dd>{selectedEvidence.sourceName}</dd>
              </div>
            </dl>
            <section className="product-document-history" aria-labelledby="document-version-history-title">
              <div className="product-document-history-heading">
                <div>
                  <p className="workspace-eyebrow">VERSION HISTORY</p>
                  <h4 id="document-version-history-title">Versions of this document</h4>
                </div>
                {versionHistory && (
                  <span>
                    {versionHistory.versions.length}{' '}
                    {versionHistory.versions.length === 1 ? 'version' : 'versions'}
                  </span>
                )}
              </div>
              {versionHistoryError ? (
                <Alert showIcon type="warning" title={versionHistoryError} />
              ) : versionHistoryBusy && versionHistory === null ? (
                <Skeleton active paragraph={{ rows: 2 }} />
              ) : versionHistory ? (
                <div className="product-document-history-list">
                  {versionHistory.versions.map((version) => {
                    const selected = version.documentVersionId === selectedEvidence.documentVersionId;
                    return (
                      <article
                        key={version.documentVersionId}
                        className={selected ? 'current' : undefined}
                      >
                        <span className="product-document-history-version">
                          <strong>Version {version.version}</strong>
                          {selected && <Tag color="blue">OPEN</Tag>}
                        </span>
                        <span className="product-document-history-copy">
                          <strong>{version.originalFileName}</strong>
                          <small>
                            {typeName(version.contentType)} · Received {formatDate(version.receivedAt)}
                          </small>
                          <small>{version.sourceName}</small>
                        </span>
                        <Button
                          type={selected ? 'default' : 'link'}
                          disabled={selected || versionHistoryBusy}
                          onClick={() => {
                            void openDocumentVersion(version.documentVersionId);
                          }}
                        >
                          {selected ? 'Current view' : 'Open version'}
                        </Button>
                      </article>
                    );
                  })}
                </div>
              ) : null}
              <p className="product-document-history-note">
                Version history comes from Evidence metadata. Opening a version rechecks your
                current document access before showing exact source details.
              </p>
            </section>

            <section className="product-document-links" aria-labelledby="document-financial-links-title">
              <div>
                <p className="workspace-eyebrow">RELATED FINANCIAL INFORMATION</p>
                <h4 id="document-financial-links-title">Trusted information using this document version</h4>
              </div>
              {!canReadFinancial ? (
                <p className="product-document-links-note">
                  Linked financial information is not included in your current access.
                </p>
              ) : financialLinkError ? (
                <Alert showIcon type="warning" title={financialLinkError} />
              ) : financialProfile === null ? (
                <p className="product-document-links-note">Refreshing linked financial information…</p>
              ) : selectedLinkedFacts.length > 0 ? (
                <div className="product-document-links-list">
                  {selectedLinkedFacts.map((fact) => (
                    <a
                      key={fact.financialFactId}
                      href={'/finances?fact=' + encodeURIComponent(fact.financialFactId)}
                    >
                      <span>
                        <strong>{fact.label}</strong>
                        <small>
                          {fact.factType === 'INCOME'
                            ? 'Income'
                            : fact.factType === 'EXPENSE'
                              ? 'Expense'
                              : fact.factType === 'LOAN' ? 'Loan information' : fact.factType === 'ASSET' ? 'Asset source figure' : fact.factType === 'INVESTMENT' ? 'Investment source figure' : fact.factType === 'INSURANCE' ? 'Insurance source figure' : fact.factType === 'TAX' ? 'Tax source figure' : fact.factType}
                        </small>
                      </span>
                      <strong>{formatFinancialValue(fact)}</strong>
                      <WorkspaceIcon name="arrow" size={15} />
                    </a>
                  ))}
                </div>
              ) : (
                <p className="product-document-links-note">
                  No trusted financial information currently uses this document version.
                </p>
              )}
            </section>
            <details className="product-review-note">
              <summary>Technical details</summary>
              <dl>
                <div>
                  <dt>Document ID</dt>
                  <dd className="product-identifier">{selectedEvidence.documentId}</dd>
                </div>
                <div>
                  <dt>Version ID</dt>
                  <dd className="product-identifier">{selectedEvidence.documentVersionId}</dd>
                </div>
                <div>
                  <dt>Evidence ID</dt>
                  <dd className="product-identifier">{selectedEvidence.evidenceId}</dd>
                </div>
                <div>
                  <dt>SHA-256</dt>
                  <dd className="product-hash">{selectedEvidence.contentSha256}</dd>
                </div>
              </dl>
            </details>
            <div className="product-processing-entry">
              <div>
                <strong>Extract details from this document</strong>
                <p>
                  {selectedEvidence.contentType === 'text/plain'
                    ? 'Find supported income and expense information and review where it came from.'
                    : 'Extracting details is currently available only for structured text files.'}
                </p>
              </div>
              <Button
                type="primary"
                disabled={!canProcess || selectedEvidence.contentType !== 'text/plain'}
                onClick={() => {
                  setProcessingEvidence(selectedEvidence);
                  setSelectedEvidence(null);
                  setProcessing(null);
                  setProcessingError('');
                  setSelectedSourceFactIds([]);
                  setPromotionError('');
                  setPromotionResult(null);
                }}
              >
                Extract details
              </Button>
            </div>
            {!canProcess && (
              <Alert
                showIcon
                type="warning"
                title="Extracting details is not included in your current access"
                description="Contact your organization administrator if you need access to extract document details."
              />
            )}
          </div>
        )}
      </Drawer>

      <Drawer
        title="Extract and review details"
        aria-label="Extract and review details"
        open={Boolean(processingEvidence)}
        onClose={closeProcessing}
        destroyOnHidden
        mask={{ closable: !processingBusy && !promotionBusy }}
        closable={!processingBusy && !promotionBusy}
        size="large"
        footer={
          <div className="product-drawer-actions">
            <Button onClick={closeProcessing} disabled={processingBusy || promotionBusy}>
              Close
            </Button>
            {!processing?.job && (
              <Button
                type="primary"
                loading={processingBusy}
                disabled={!canProcess || processingEvidence?.contentType !== 'text/plain'}
                onClick={() => void processDocument()}
              >
                Extract details
              </Button>
            )}
            {processing?.job.state === 'Completed' &&
              processing.facts.length > 0 &&
              !promotionResult && (
                <Button
                  type="primary"
                  loading={promotionBusy}
                  disabled={!canUpdateFinancial || selectedSourceFactIds.length === 0}
                  onClick={() => void addReviewedFacts()}
                >
                  Add selected information
                </Button>
              )}
            {promotionResult && (
              <Button type="primary" href="/finances">
                View financial information
              </Button>
            )}
          </div>
        }
      >
        {processingEvidence && (
          <div className="product-processing-review">
            <div className="product-processing-hero">
              <span className="product-review-icon">
                <WorkspaceIcon name="workflow" size={25} />
              </span>
              <div>
                <Tag color={processing?.job.state === 'Completed' ? 'success' : 'processing'}>
                  {processing?.job.state === 'Completed'
                    ? 'DETAILS EXTRACTED'
                    : processing?.job.state === 'Failed'
                      ? 'NEEDS ATTENTION'
                      : processing?.job.state === 'Running'
                        ? 'EXTRACTING DETAILS'
                        : 'READY TO EXTRACT'}
                </Tag>
                <h3>{processingEvidence.originalFileName}</h3>
                <p>
                  Version {processingEvidence.version} · {typeName(processingEvidence.contentType)}
                </p>
              </div>
            </div>
            <Alert
              showIcon
              type="info"
              title="Review before adding anything to your financial information"
              description="Monergy can find supported income and expense details in this document. Review them against the document first. Your financial information is not changed by this step."
            />
            {!canUpdateFinancial && processing?.job.state === 'Completed' && (
              <Alert
                showIcon
                type="warning"
                title="Adding financial information isn't included in your current access"
                description="Your organization administrator can review the current Customer access package. Extracted details remain review-only until updating financial information is included in your access."
              />
            )}
            {promotionError && <Alert showIcon type="error" title={promotionError} />}
            {promotionResult && (
              <Alert
                showIcon
                type="success"
                title="Your financial information was updated"
                description={
                  String(promotionResult.confirmed) +
                  ' reviewed ' +
                  (promotionResult.confirmed === 1 ? 'item confirmed' : 'items confirmed') +
                  ' · profile revision ' +
                  String(promotionResult.profile.revision)
                }
              />
            )}
            {processingBusy && (
              <div className="product-processing-progress" aria-live="polite">
                <Spin size="small" />
                <p>Extracting details from this document…</p>
              </div>
            )}
            {processingError && <Alert showIcon type="error" title={processingError} />}
            {processing?.job.state === 'Failed' && !processingError && (
              <Alert
                showIcon
                type="error"
                title="We couldn't extract details from this document"
                description={
                  'Check that this is a supported structured-text document containing income or expense information, then try again.'
                }
              />
            )}
            {processing?.facts.length ? (
              <section className="product-facts" aria-labelledby="source-facts-title">
                <div className="product-section-heading">
                  <div>
                    <p className="workspace-eyebrow">INFORMATION TO REVIEW</p>
                    <h2 id="source-facts-title">Review information found in this document</h2>
                  </div>
                  <span>{processing.facts.length} items</span>
                </div>
                <div className="product-fact-list">
                  {processing.facts.map((fact) => (
                    <article
                      key={fact.sourceFactId}
                      className={
                        selectedSourceFactIds.includes(fact.sourceFactId)
                          ? 'product-fact-card selected'
                          : 'product-fact-card'
                      }
                    >
                      <Checkbox
                        checked={selectedSourceFactIds.includes(fact.sourceFactId)}
                        disabled={promotionBusy || Boolean(promotionResult)}
                        onChange={(event) =>
                          toggleSourceFact(fact.sourceFactId, event.target.checked)
                        }
                        aria-label={'Add ' + fact.label + ' to financial information'}
                      />
                      <div>
                        <Tag>
                          {fact.factType === 'INCOME'
                            ? 'Income'
                            : fact.factType === 'EXPENSE'
                              ? 'Expense'
                              : fact.factType === 'LOAN' ? 'Loan information' : fact.factType === 'ASSET' ? 'Asset source figure' : fact.factType === 'INVESTMENT' ? 'Investment source figure' : fact.factType === 'INSURANCE' ? 'Insurance source figure' : fact.factType === 'TAX' ? 'Tax source figure' : fact.factType}
                        </Tag>
                        <strong>{fact.label}</strong>
                        <span>
                          {fact.sourceLocation} · {fact.effectiveDate}
                        </span>
                      </div>
                      <div className="product-fact-value">
                        <strong>
                          {new Intl.NumberFormat(undefined, {
                            style: 'currency',
                            currency: fact.currency,
                          }).format(fact.candidateValue)}
                        </strong>
                        <span>{Math.round(fact.confidence * 100)}% confidence</span>
                      </div>
                    </article>
                  ))}
                </div>
                <p className="product-review-note">
                  Each item stays linked to the document location it came from. Review the details
                  carefully. Select only the items you have reviewed, then use Add selected information. Nothing is added before that confirmation.
                </p>
              </section>
            ) : (
              !processingBusy &&
              !processing?.job && (
                <div className="product-processing-steps">
                  <div>
                    <span>01</span>
                    <strong>Start</strong>
                    <p>Choose the document details you want Monergy to extract.</p>
                  </div>
                  <div>
                    <span>02</span>
                    <strong>Check</strong>
                    <p>Monergy checks your current access and the selected document.</p>
                  </div>
                  <div>
                    <span>03</span>
                    <strong>Review</strong>
                    <p>Check the extracted information against the document before future use.</p>
                  </div>
                </div>
              )
            )}
          </div>
        )}
      </Drawer>

      <Drawer
        title="Review document upload"
        aria-label="Review document upload"
        open={Boolean(candidate)}
        onClose={close}
        destroyOnHidden
        mask={{ closable: !busy }}
        closable={!busy}
        size="default"
        footer={
          <div className="product-drawer-actions">
            <Button onClick={close} disabled={busy}>
              Cancel
            </Button>
            <Button
              type="primary"
              onClick={() => void upload()}
              loading={busy}
              disabled={Boolean(issue) || !canUpload}
            >
              Upload document
            </Button>
          </div>
        }
      >
        {candidate && (
          <div className="product-review">
            <span className="product-review-icon">
              <WorkspaceIcon name="evidence" size={25} />
            </span>
            <h3>{candidate.name}</h3>
            <p>
              {typeName(candidate.type || 'Unknown type')} · {formatBytes(candidate.size)}
            </p>
            {issue && <Alert showIcon type="error" title={issue} />}
            {error && <Alert showIcon type="error" title={error} />}
            {busy && (
              <div className="product-processing-progress" aria-live="polite">
                <Spin size="small" />
                <p>Uploading document…</p>
              </div>
            )}
            <dl>
              <div>
                <dt>Customer</dt>
                <dd>{product?.customerName}</dd>
              </div>
              <div>
                <dt>Result</dt>
                <dd>New document · Version 1</dd>
              </div>
              <div>
                <dt>Storage</dt>
                <dd>Test environment · Temporary data</dd>
              </div>
            </dl>
            <p className="product-review-note">
              Monergy checks your current access before saving this document version.
            </p>
          </div>
        )}
      </Drawer>
    </main>
  );
}
