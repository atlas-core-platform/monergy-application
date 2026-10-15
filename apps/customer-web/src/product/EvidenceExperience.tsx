import { WorkspaceIcon } from '@monergy/ui-foundation';
import { Alert, Button, Drawer, Input, Select, Skeleton, Spin, Tag, Upload } from 'antd';
import type { UploadProps } from 'antd';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { AccessApiError } from '../access/accessApi';
import { useWorkspaceSession } from '../access/workspaceSession';
import {
  evidenceTypes,
  getDocumentProcessing,
  listEvidence,
  maximumEvidenceBytes,
  scheduleDocumentProcessing,
  submitEvidence,
  type ProductEvidenceItem,
  type ProductEvidenceReceipt,
  type ProductProcessingView,
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

export default function EvidenceExperience() {
  const { api, authorization } = useWorkspaceSession();
  const product = authorization?.area === 'product' ? authorization.context : null;
  const canUpload = product?.capabilities.includes('evidence.document.upload') ?? false;
  const canRead = product?.capabilities.includes('evidence.document.read') ?? false;
  const canProcess =
    product?.capabilities.includes('document-intelligence.document.process') ?? false;
  const [candidate, setCandidate] = useState<File | null>(null);
  const [operationId, setOperationId] = useState('');
  const [evidence, setEvidence] = useState<ProductEvidenceItem[]>([]);
  const [confirmation, setConfirmation] = useState<ProductEvidenceReceipt | null>(null);
  const [libraryBusy, setLibraryBusy] = useState(false);
  const [libraryError, setLibraryError] = useState('');
  const [query, setQuery] = useState('');
  const [typeFilter, setTypeFilter] = useState('all');
  const [selectedEvidence, setSelectedEvidence] = useState<ProductEvidenceItem | null>(null);
  const [processingEvidence, setProcessingEvidence] = useState<ProductEvidenceItem | null>(null);
  const [processing, setProcessing] = useState<ProductProcessingView | null>(null);
  const [processingBusy, setProcessingBusy] = useState(false);
  const [processingError, setProcessingError] = useState('');
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
  const visibleEvidence = useMemo(() => {
    const term = query.trim().toLocaleLowerCase();
    return evidence.filter(
      (item) =>
        (typeFilter === 'all' || item.contentType === typeFilter) &&
        (!term ||
          item.originalFileName.toLocaleLowerCase().includes(term) ||
          item.documentVersionId.toLocaleLowerCase().includes(term)),
    );
  }, [evidence, query, typeFilter]);
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
  const closeProcessing = () => {
    if (processingBusy) return;
    setProcessingEvidence(null);
    setProcessing(null);
    setProcessingError('');
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
            : "We couldn't extract details from this document. Your financial profile has not changed.",
      );
    } finally {
      controller.abort();
      setProcessingBusy(false);
    }
  };

  return (
    <main className="product-page evidence-page">
      <header className="product-heading">
        <div>
          <p className="workspace-eyebrow">DOCUMENTS</p>
          <h1>Keep your financial documents together.</h1>
          <p>
            Add documents to {product?.customerName ?? 'your workspace'} and review the information
            Monergy can find in them.
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
                    <small>{formatDate(item.createdAt)}</small>
                  </span>
                  <WorkspaceIcon name="arrow" size={17} />
                </button>
              </article>
            ))}
          </div>
        )}
      </section>

      <Drawer
        title="Document details"
        aria-label="Document details"
        open={Boolean(selectedEvidence)}
        onClose={() => {
          setSelectedEvidence(null);
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
            </dl>
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
        mask={{ closable: !processingBusy }}
        closable={!processingBusy}
        size="large"
        footer={
          <div className="product-drawer-actions">
            <Button onClick={closeProcessing} disabled={processingBusy}>
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
              title="Review before adding anything to your financial profile"
              description="Monergy can find supported income and expense details in this document. Review them against the document first. Your financial profile is not changed by this step."
            />
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
                    <article key={fact.sourceFactId} className="product-fact-card">
                      <div>
                        <Tag>{fact.factType}</Tag>
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
                  carefully; nothing here has been added to your financial profile.
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
