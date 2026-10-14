import { WorkspaceIcon } from '@monergy/ui-foundation';
import { Alert, Button, Drawer, Input, Progress, Select, Skeleton, Tag, Upload } from 'antd';
import type { UploadProps } from 'antd';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { AccessApiError } from '../access/accessApi';
import { useWorkspaceSession } from '../access/workspaceSession';
import {
  evidenceTypes,
  listEvidence,
  maximumEvidenceBytes,
  submitEvidence,
  type ProductEvidenceItem,
  type ProductEvidenceReceipt,
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
  const [candidate, setCandidate] = useState<File | null>(null);
  const [operationId, setOperationId] = useState('');
  const [evidence, setEvidence] = useState<ProductEvidenceItem[]>([]);
  const [confirmation, setConfirmation] = useState<ProductEvidenceReceipt | null>(null);
  const [libraryBusy, setLibraryBusy] = useState(false);
  const [libraryError, setLibraryError] = useState('');
  const [query, setQuery] = useState('');
  const [typeFilter, setTypeFilter] = useState('all');
  const [selectedEvidence, setSelectedEvidence] = useState<ProductEvidenceItem | null>(null);
  const [busy, setBusy] = useState(false);
  const [progress, setProgress] = useState(0);
  const [error, setError] = useState('');
  const timer = useRef<number | null>(null);
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
              ? 'Your current role does not allow evidence library access.'
              : 'The Evidence owner could not confirm the library. Retry before relying on this view.',
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
    setProgress(18);
    timer.current = window.setInterval(() => {
      setProgress((value) => Math.min(value + 8, 82));
    }, 180);
    try {
      const receipt = await submitEvidence(api, operationId, candidate);
      setProgress(100);
      setConfirmation(receipt);
      if (receipt.documentVersionId) await loadLibrary();
      window.setTimeout(close, 250);
    } catch (failure) {
      setError(
        failure instanceof AccessApiError && failure.status === 403
          ? 'Your current role does not allow evidence uploads.'
          : failure instanceof AccessApiError && failure.status === 409
            ? 'This upload identity was already used for different content. Select the file again.'
            : 'The evidence owner did not confirm this upload. Retry to check the same operation safely.',
      );
      setProgress(0);
    } finally {
      window.clearInterval(timer.current);
      timer.current = null;
      setBusy(false);
    }
  };

  return (
    <main className="product-page evidence-page">
      <header className="product-heading">
        <div>
          <p className="workspace-eyebrow">EVIDENCE</p>
          <h1>Bring your financial documents into view.</h1>
          <p>
            Add source documents to {product?.customerName ?? 'your workspace'}. Monergy verifies
            the file, records its evidence identity and preserves its source hash.
          </p>
        </div>
        <Tag className="product-environment">LOCAL UAT · RUNTIME ONLY</Tag>
      </header>

      <section className="product-intake-grid" aria-labelledby="upload-heading">
        <div className="product-panel product-upload-panel">
          <div className="product-section-title">
            <span className="product-icon">
              <WorkspaceIcon name="evidence" size={20} />
            </span>
            <div>
              <h2 id="upload-heading">Add evidence</h2>
              <p>PDF, PNG, JPEG or text · up to 1 MB</p>
            </div>
          </div>
          {!canUpload && (
            <Alert
              showIcon
              type="warning"
              title="Evidence upload is not included in your current role"
              description="Ask a Tenant Administrator to assign a role that includes Upload evidence."
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
              <WorkspaceIcon name="shield" size={16} /> Customer ownership checked
            </span>
            <span>
              <WorkspaceIcon name="check" size={16} /> Access evaluated at submission
            </span>
          </div>
        </div>
        <aside className="product-panel product-explainer" aria-labelledby="evidence-result-title">
          <p className="workspace-eyebrow">WHAT HAPPENS NEXT</p>
          <h2 id="evidence-result-title">A traceable source, ready for the next step.</h2>
          <ol>
            <li>
              <span>01</span>
              <div>
                <strong>Verify</strong>
                <p>File type, size and content hash are checked.</p>
              </div>
            </li>
            <li>
              <span>02</span>
              <div>
                <strong>Register</strong>
                <p>The Evidence owner creates a document version.</p>
              </div>
            </li>
            <li>
              <span>03</span>
              <div>
                <strong>Confirm</strong>
                <p>You receive the exact evidence and version references.</p>
              </div>
            </li>
          </ol>
          <p className="product-limit-note">
            This LOCAL/UAT slice keeps uploaded content only for the current runtime. Production
            retention remains a separate governed decision.
          </p>
        </aside>
      </section>

      <section className="product-recent" aria-labelledby="evidence-library-title">
        <div className="product-section-heading">
          <div>
            <p className="workspace-eyebrow">EVIDENCE LIBRARY</p>
            <h2 id="evidence-library-title">Verified sources and provenance</h2>
          </div>
          <span>
            {evidence.length ? `${String(evidence.length)} available` : 'No evidence yet'}
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
            title={`${confirmation.originalFileName} was registered`}
            description={`Evidence version ${confirmation.documentVersionId} was confirmed by the Evidence owner.`}
            className="product-library-alert"
          />
        )}
        {!canRead && (
          <Alert
            showIcon
            type="warning"
            title="Evidence library access is not included in your current role"
            description="You may still upload evidence when separately authorized, but existing documents cannot be displayed."
            className="product-library-alert"
          />
        )}
        {canRead && (
          <div className="product-library-controls">
            <Input
              allowClear
              aria-label="Find evidence"
              placeholder="Find by file name or version ID"
              value={query}
              onChange={(event) => {
                setQuery(event.target.value);
              }}
            />
            <Select
              id="evidence-type-filter"
              aria-label="Filter evidence type"
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
              <strong>Your verified evidence library is ready.</strong>
              <p>Add a source document above to create its first traceable version.</p>
            </div>
          </div>
        ) : canRead && visibleEvidence.length === 0 ? (
          <div className="product-empty">
            <WorkspaceIcon name="search" size={24} />
            <div>
              <strong>No evidence matches these filters.</strong>
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
                  aria-label={`View provenance for ${item.originalFileName}`}
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
                    <Tag color="success">VERIFIED</Tag>
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
        title="Evidence provenance"
        aria-label="Evidence provenance"
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
            <Tag color="success">OWNER VERIFIED</Tag>
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
                <dt>Registered</dt>
                <dd>{formatDate(selectedEvidence.createdAt)}</dd>
              </div>
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
            <p className="product-review-note">
              These identifiers and the source hash came from the Evidence owner. Raw document
              content is not exposed by this library view.
            </p>
          </div>
        )}
      </Drawer>

      <Drawer
        title="Review evidence upload"
        aria-label="Review evidence upload"
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
              Upload evidence
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
              <Progress percent={progress} status="active" aria-label="Evidence upload progress" />
            )}
            <dl>
              <div>
                <dt>Customer</dt>
                <dd>{product?.customerName}</dd>
              </div>
              <div>
                <dt>Result</dt>
                <dd>New evidence document · Version 1</dd>
              </div>
              <div>
                <dt>Storage</dt>
                <dd>Current LOCAL/UAT runtime only</dd>
              </div>
            </dl>
            <p className="product-review-note">
              Uploading sends the file to the Evidence service. Your session, customer ownership and
              upload capability are checked again before the version is registered.
            </p>
          </div>
        )}
      </Drawer>
    </main>
  );
}
