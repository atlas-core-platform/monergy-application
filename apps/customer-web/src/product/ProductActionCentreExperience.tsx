import { WorkspaceIcon } from '@monergy/ui-foundation';
import { Alert, Button, Skeleton, Tag } from 'antd';
import { useEffect, useMemo, useState } from 'react';
import { useWorkspaceSession } from '../access/workspaceSession';
import {
  getFinancialProfile,
  listEvidence,
  type ProductFinancialProfile,
} from './productApi';
import './product.css';

interface AttentionItem {
  id: string;
  title: string;
  description: string;
  href?: string;
  action?: string;
}

export default function ProductActionCentreExperience() {
  const { api, authorization } = useWorkspaceSession();
  const product = authorization?.area === 'product' ? authorization.context : null;
  const capabilities = new Set(product?.capabilities ?? []);
  const canReadDocuments = capabilities.has('evidence.document.read');
  const canUploadDocuments = capabilities.has('evidence.document.upload');
  const canReadFinancial = capabilities.has('financial-profile.profile.read');
  const [documentCount, setDocumentCount] = useState<number | null>(null);
  const [profile, setProfile] = useState<ProductFinancialProfile | null>(null);
  const [busy, setBusy] = useState(false);
  const [warnings, setWarnings] = useState<string[]>([]);

  useEffect(() => {
    if (!api || (!canReadDocuments && !canReadFinancial)) return;
    const controller = new AbortController();
    setBusy(true);
    setWarnings([]);

    void Promise.allSettled([
      canReadDocuments ? listEvidence(api, controller.signal) : Promise.resolve(null),
      canReadFinancial ? getFinancialProfile(api, controller.signal) : Promise.resolve(null),
    ])
      .then(([documents, financial]) => {
        if (controller.signal.aborted) return;

        const nextWarnings: string[] = [];

        if (documents.status === 'fulfilled') {
          setDocumentCount(documents.value?.items.length ?? null);
        } else if (canReadDocuments) {
          setDocumentCount(null);
          nextWarnings.push("We couldn't refresh your Documents.");
        }

        if (financial.status === 'fulfilled') {
          setProfile(financial.value);
        } else if (canReadFinancial) {
          setProfile(null);
          nextWarnings.push("We couldn't refresh your trusted financial information.");
        }

        setWarnings(nextWarnings);
      })
      .finally(() => {
        if (!controller.signal.aborted) setBusy(false);
      });

    return () => controller.abort();
  }, [api, canReadDocuments, canReadFinancial]);

  const state = useMemo(() => {
    const facts = profile?.facts ?? [];
    const income = facts.filter((fact) => fact.factType === 'INCOME').length;
    const expenses = facts.filter((fact) => fact.factType === 'EXPENSE').length;
    const covered = Number(income > 0) + Number(expenses > 0);
    return {
      trusted: facts.length,
      income,
      expenses,
      covered,
    };
  }, [profile]);

  const attention = useMemo<AttentionItem[]>(() => {
    const items: AttentionItem[] = [];

    if (canReadDocuments && documentCount === 0) {
      items.push({
        id: 'add-document',
        title: 'No financial documents have been added yet.',
        description:
          'Add a supported document when you are ready. Uploading a document does not change your Financial information until you review and add supported details.',
        href: canUploadDocuments ? '/evidence' : undefined,
        action: canUploadDocuments ? 'Add document' : undefined,
      });
      return items;
    }

    if (
      canReadDocuments &&
      canReadFinancial &&
      profile !== null &&
      documentCount !== null &&
      documentCount > 0 &&
      state.trusted === 0
    ) {
      items.push({
        id: 'review-documents',
        title: 'Your Documents do not contain any trusted financial information yet.',
        description:
          'Review a supported document and add only the Income or Expense details you have checked. Extracted information remains separate until you confirm it.',
        href: '/evidence',
        action: 'Review Documents',
      });
      return items;
    }

    if (canReadFinancial && state.trusted > 0) {
      if (state.income === 0) {
        items.push({
          id: 'income-missing',
          title: 'No trusted Income information is currently represented.',
          description:
            'The supported Financial Passport currently has no trusted Income items. Review Documents if you have an Income source you want to add.',
          href: canReadDocuments ? '/evidence' : undefined,
          action: canReadDocuments ? 'Review Documents' : undefined,
        });
      }

      if (state.expenses === 0) {
        items.push({
          id: 'expense-missing',
          title: 'No trusted Expense information is currently represented.',
          description:
            'The supported Financial Passport currently has no trusted Expense items. Review Documents if you have an Expense source you want to add.',
          href: canReadDocuments ? '/evidence' : undefined,
          action: canReadDocuments ? 'Review Documents' : undefined,
        });
      }
    }

    return items;
  }, [
    canReadDocuments,
    canReadFinancial,
    canUploadDocuments,
    documentCount,
    profile,
    state.expenses,
    state.income,
    state.trusted,
  ]);

  const hasReadableState = canReadDocuments || canReadFinancial;
  const attentionStateComplete =
    canReadDocuments &&
    canReadFinancial &&
    documentCount !== null &&
    profile !== null;

  return (
    <main className="product-page product-action-centre">
      <header className="product-heading">
        <div>
          <p className="workspace-eyebrow">ACTION CENTRE</p>
          <h1>See what needs attention in the journeys Monergy supports today.</h1>
          <p>
            These attention items are derived only from your current Documents and trusted Income
            or Expense information. They are not ranked recommendations, a financial-health score,
            or a persistent task list.
          </p>
        </div>
        <Tag className="product-environment">
          {(product?.environment ?? 'Test environment').toUpperCase()} ·{' '}
          {(product?.dataDurability ?? 'Temporary data').toUpperCase()}
        </Tag>
      </header>

      {!hasReadableState && (
        <Alert
          showIcon
          type="warning"
          title="No supported attention information is included in your current access"
          description="Your organization administrator can review your Customer access package."
        />
      )}

      {warnings.map((warning) => (
        <Alert key={warning} showIcon type="warning" title={warning} />
      ))}

      {busy && documentCount === null && profile === null ? (
        <section className="product-panel">
          <Skeleton active paragraph={{ rows: 5 }} />
        </section>
      ) : hasReadableState ? (
        <>
          <section className="product-action-summary" aria-label="Current supported workspace state">
            <div className="product-panel product-summary-card">
              <span className="product-icon">
                <WorkspaceIcon name="evidence" size={20} />
              </span>
              <div>
                <span>Documents</span>
                <strong>{canReadDocuments ? (documentCount ?? '—') : '—'}</strong>
              </div>
            </div>
            <div className="product-panel product-summary-card">
              <span className="product-icon">
                <WorkspaceIcon name="reports" size={20} />
              </span>
              <div>
                <span>Trusted information</span>
                <strong>{canReadFinancial && profile !== null ? state.trusted : '—'}</strong>
              </div>
            </div>
            <div className="product-panel product-summary-card">
              <span className="product-icon">
                <WorkspaceIcon name="check" size={20} />
              </span>
              <div>
                <span>Supported coverage</span>
                <strong>{canReadFinancial && profile !== null ? String(state.covered) + ' of 2' : '—'}</strong>
              </div>
            </div>
          </section>

          <section className="product-panel product-action-list-panel">
            <div className="product-section-heading">
              <div>
                <p className="workspace-eyebrow">CURRENT ATTENTION</p>
                <h2>
                  {attention.length > 0
                    ? attention.length +
                      ' ' +
                      (attention.length === 1 ? 'item' : 'items') +
                      ' from supported journeys'
                    : attentionStateComplete
                      ? 'Nothing currently needs attention in the supported journeys'
                      : 'Current attention could not be fully determined'}
                </h2>
              </div>
              <span>Deterministic · not ranked</span>
            </div>

            {attention.length > 0 ? (
              <div className="product-action-list">
                {attention.map((item) => (
                  <article className="product-action-item" key={item.id}>
                    <span className="product-action-icon">
                      <WorkspaceIcon name="changes" size={19} />
                    </span>
                    <div>
                      <h3>{item.title}</h3>
                      <p>{item.description}</p>
                    </div>
                    {item.href && item.action && (
                      <Button href={item.href}>{item.action}</Button>
                    )}
                  </article>
                ))}
              </div>
            ) : !attentionStateComplete ? (
              <div className="product-action-clear">
                <WorkspaceIcon name="changes" size={23} />
                <div>
                  <strong>Some current information is unavailable.</strong>
                  <p>
                    Monergy will not infer a clear or missing-information state until the supported
                    Documents and Financial information needed for this view can be refreshed.
                  </p>
                </div>
              </div>
            ) : (
              <div className="product-action-clear">
                <WorkspaceIcon name="check" size={23} />
                <div>
                  <strong>No current attention items from Documents or supported Financial information.</strong>
                  <p>
                    This does not mean your overall finances are complete. It only reflects the
                    Income and Expense journeys Monergy currently supports.
                  </p>
                </div>
                <Button href="/finances">View Financial information</Button>
              </div>
            )}
          </section>

          <section className="product-panel product-action-boundary">
            <WorkspaceIcon name="shield" size={20} />
            <div>
              <p className="workspace-eyebrow">BOUNDARY</p>
              <h2>Attention is not advice.</h2>
              <p>
                Monergy does not infer urgency, financial risk, missing life goals, or recommended
                actions here. Future tasks, information requests, lending steps, professional
                reviews, and provider issues require their own implemented owners and governed rules.
              </p>
            </div>
          </section>
        </>
      ) : null}
    </main>
  );
}
