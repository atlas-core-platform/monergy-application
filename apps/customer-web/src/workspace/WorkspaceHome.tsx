import { WorkspaceIcon } from '@monergy/ui-foundation';
import type { WorkspaceIconName } from '@monergy/ui-foundation';
import { Alert, Skeleton, Tag } from 'antd';
import { useEffect, useMemo, useState } from 'react';
import { useWorkspaceSession } from '../access/workspaceSession';
import { getFinancialProfile, listEvidence, type ProductFinancialProfile } from '../product/productApi';

const journeys: {
  title: string;
  description: string;
  href: string;
  icon: WorkspaceIconName;
  capability: string;
  action: string;
}[] = [
  {
    title: 'Add financial documents',
    description: 'Upload a document and keep its source details connected for later review.',
    href: '/evidence',
    icon: 'evidence',
    capability: 'evidence.document.upload',
    action: 'Add document',
  },
  {
    title: 'Review current attention',
    description: 'See bounded attention items from current Documents, trusted financial information and recent processing status.',
    href: '/actions',
    icon: 'changes',
    capability: 'financial-profile.profile.read',
    action: 'Open Action Centre',
  },
  {
    title: 'Review financial information',
    description: 'See trusted Income, Expense, Loan, Asset, Investment and Insurance source information linked to its document.',
    href: '/finances',
    icon: 'reports',
    capability: 'financial-profile.profile.read',
    action: 'View financial information',
  },
  {
    title: 'Search your information',
    description: 'Find authorized information and follow each result back to its source.',
    href: '/search',
    icon: 'search',
    capability: 'search.query.execute',
    action: 'Open search',
  },
  {
    title: 'Create a basic financial summary',
    description: 'Bring your trusted financial information into one source-connected exportable view.',
    href: '/reports',
    icon: 'reports',
    capability: 'reporting.report.generate',
    action: 'Create summary',
  },
];

const formatPassportDate = (value: string | null) =>
  value
    ? new Intl.DateTimeFormat('en-GB', {
        day: 'numeric',
        month: 'short',
        year: 'numeric',
        timeZone: 'UTC',
      }).format(new Date(value))
    : 'No trusted updates yet';

export default function WorkspaceHome() {
  const { api, authorization } = useWorkspaceSession();
  const context = authorization?.area === 'product' ? authorization.context : null;
  const capabilities = new Set(context?.capabilities ?? journeys.map((item) => item.capability));
  const available = journeys.filter((item) => capabilities.has(item.capability));
  const canReadFinancial = capabilities.has('financial-profile.profile.read');
  const canReadDocuments = capabilities.has('evidence.document.read');
  const [profile, setProfile] = useState<ProductFinancialProfile | null>(null);
  const [documentCount, setDocumentCount] = useState<number | null>(null);
  const [snapshotBusy, setSnapshotBusy] = useState(false);
  const [snapshotError, setSnapshotError] = useState('');

  useEffect(() => {
    if (!api || (!canReadFinancial && !canReadDocuments)) return;
    const controller = new AbortController();
    setSnapshotBusy(true);
    setSnapshotError('');
    void Promise.all([
      canReadFinancial ? getFinancialProfile(api, controller.signal) : Promise.resolve(null),
      canReadDocuments ? listEvidence(api, controller.signal) : Promise.resolve(null),
    ])
      .then(([financial, documents]) => {
        setProfile(financial);
        setDocumentCount(documents?.items.length ?? null);
      })
      .catch(() => {
        if (!controller.signal.aborted)
          setSnapshotError("We couldn't refresh your financial snapshot. Your detailed pages are still available.");
      })
      .finally(() => {
        if (!controller.signal.aborted) setSnapshotBusy(false);
      });
    return () => controller.abort();
  }, [api, canReadDocuments, canReadFinancial]);

  const snapshot = useMemo(() => {
    const facts = profile?.facts ?? [];
    const income = facts.filter((fact) => fact.factType === 'INCOME').length;
    const expenses = facts.filter((fact) => fact.factType === 'EXPENSE').length;
    const loans = facts.filter((fact) => fact.factType === 'LOAN').length;
    const assets = facts.filter((fact) => fact.factType === 'ASSET').length;
    const investments = facts.filter((fact) => fact.factType === 'INVESTMENT').length;
    const insurance = facts.filter((fact) => fact.factType === 'INSURANCE').length;
    const sources = new Set(facts.map((fact) => fact.source.documentVersion)).size;
    const ordered = [...facts].sort((left, right) =>
      right.source.recordedAt.localeCompare(left.source.recordedAt),
    );
    const latest = ordered.slice(0, 3);
    const coverage = [
      { key: 'INCOME', label: 'Income', count: income },
      { key: 'EXPENSE', label: 'Expenses', count: expenses },
      { key: 'LOAN', label: 'Loan information', count: loans },
      { key: 'ASSET', label: 'Asset source figures', count: assets },
      { key: 'INVESTMENT', label: 'Investment source figures', count: investments },
      { key: 'INSURANCE', label: 'Insurance source figures', count: insurance },
    ];
    return {
      facts: facts.length,
      income,
      expenses,
      loans,
      assets,
      investments,
      insurance,
      sources,
      latest,
      latestRecordedAt: ordered[0]?.source.recordedAt ?? null,
      coverage,
      coveredCategories: coverage.filter((item) => item.count > 0).length,
    };
  }, [profile]);

  const hasTrustedInformation = snapshot.facts > 0;
  return (
    <main className="workspace-home product-home">
      <div className="workspace-page-heading">
        <div>
          <p className="workspace-eyebrow">MY FINANCES</p>
          <h1>Good {new Date().getUTCHours() < 12 ? 'morning' : 'to see you'}.</h1>
          <p className="workspace-subtitle">
            {context?.customerName ?? 'Your Monergy workspace'} · Bring your financial information
            together and keep every result connected to its source.
          </p>
        </div>
        <Tag className="product-environment">TEST ENVIRONMENT · TEMPORARY DATA</Tag>
      </div>

      <section className="workspace-hero" aria-labelledby="workspace-hero-title">
        <div className="workspace-hero-copy">
          <span className="workspace-pill">YOUR NEXT BEST STEP</span>
          <h2 id="workspace-hero-title">Start with what you already have.</h2>
          <p>
            Add a financial document. Monergy keeps the document and source details connected so
            you can review extracted Income, Expense, Loan, Asset, Investment or Insurance source information before explicitly adding it to
            your trusted financial information.
          </p>
          {capabilities.has('evidence.document.upload') ? (
            <a className="workspace-primary-link" href="/evidence">
              Add financial document <WorkspaceIcon name="arrow" size={18} />
            </a>
          ) : (
            <span className="workspace-unavailable">
              Your current access does not include document upload.
            </span>
          )}
          <span className="workspace-hero-note">
            <WorkspaceIcon name="shield" size={14} />
            Your identity, customer ownership and current access are checked by Monergy.
          </span>
        </div>
        <div className="workspace-hero-art" aria-hidden="true">
          <div className="workspace-art-grid" />
          <div className="workspace-orbit workspace-orbit-outer" />
          <div className="workspace-orbit workspace-orbit-inner" />
          <div className="workspace-art-core">
            <WorkspaceIcon name="evidence" size={39} />
          </div>
          <div className="workspace-art-chip chip-people">
            <span>
              <WorkspaceIcon name="evidence" />
            </span>
            <div>
              <strong>Document</strong>
              <small>Your original source</small>
            </div>
          </div>
          <div className="workspace-art-chip chip-permissions">
            <span>
              <WorkspaceIcon name="check" />
            </span>
            <div>
              <strong>Checked</strong>
              <small>Version and integrity recorded</small>
            </div>
          </div>
          <div className="workspace-art-chip chip-workspace">
            <span>
              <WorkspaceIcon name="changes" />
            </span>
            <div>
              <strong>Reviewed</strong>
              <small>You choose what becomes trusted information</small>
            </div>
          </div>
        </div>
      </section>

      {(canReadFinancial || canReadDocuments) && (
        <section className="workspace-passport" aria-labelledby="workspace-passport-title">
          <div className="workspace-section-heading">
            <div>
              <p className="workspace-eyebrow">FINANCIAL PASSPORT</p>
              <h2 id="workspace-passport-title">Your source-connected snapshot</h2>
            </div>
            <a href="/finances">View financial information</a>
          </div>
          {snapshotError && <Alert showIcon type="warning" title={snapshotError} />}
          {snapshotBusy && !profile && documentCount === null ? (
            <div className="workspace-passport-panel">
              <Skeleton active paragraph={{ rows: 2 }} />
            </div>
          ) : (
            <>
              <div className="workspace-passport-grid">
                <article className="workspace-passport-card">
                  <span>Trusted information</span>
                  <strong>{canReadFinancial ? snapshot.facts : '—'}</strong>
                  <small>{canReadFinancial ? 'Reviewed items in your financial profile' : 'Not included in your access'}</small>
                </article>
                <article className="workspace-passport-card">
                  <span>Income items</span>
                  <strong>{canReadFinancial ? snapshot.income : '—'}</strong>
                  <small>Source-connected Income records</small>
                </article>
                <article className="workspace-passport-card">
                  <span>Expense items</span>
                  <strong>{canReadFinancial ? snapshot.expenses : '—'}</strong>
                  <small>Source-connected Expense records</small>
                </article>
                <article className="workspace-passport-card">
                  <span>Loan information items</span>
                  <strong>{canReadFinancial ? snapshot.loans : '—'}</strong>
                  <small>Source-connected Loan fact records, not an outstanding-debt total</small>
                </article>
                <article className="workspace-passport-card">
                  <span>Documents</span>
                  <strong>{documentCount ?? '—'}</strong>
                  <small>{canReadDocuments ? 'Documents available to you' : 'Not included in your access'}</small>
                </article>
              </div>
              {canReadFinancial && (
                <div className="workspace-passport-panel workspace-passport-status">
                  <div className="workspace-passport-heading">
                    <div>
                      <p className="workspace-eyebrow">CURRENT COVERAGE &amp; FRESHNESS</p>
                      <h3>
                        {snapshot.coveredCategories} of {snapshot.coverage.length} currently supported
                        categories represented
                      </h3>
                    </div>
                    <span>
                      {snapshot.latestRecordedAt
                        ? `Last trusted update ${formatPassportDate(snapshot.latestRecordedAt)}`
                        : 'No trusted information yet'}
                    </span>
                  </div>
                  <div className="workspace-passport-coverage" aria-label="Financial Passport current coverage">
                    {snapshot.coverage.map((item) => (
                      <a
                        key={item.label}
                        className={item.count > 0 ? 'present' : 'missing'}
                        href={'/finances?type=' + encodeURIComponent(item.key)}
                        aria-label={'View ' + item.label + ' financial information'}
                      >
                        <span className="workspace-passport-coverage-icon">
                          <WorkspaceIcon name={item.count > 0 ? 'check' : 'changes'} size={16} />
                        </span>
                        <span>
                          <strong>{item.label}</strong>
                          <small>
                            {item.count > 0
                              ? `${item.count} trusted ${item.count === 1 ? 'item' : 'items'}`
                              : 'No trusted items yet'}
                          </small>
                        </span>
                        <WorkspaceIcon name="arrow" size={15} />
                      </a>
                    ))}
                  </div>
                  <p className="workspace-passport-scope-note">
                    Coverage is limited to Income, Expense, Loan, Asset, Investment and Insurance source facts. Asset and Investment figures are document-stated amounts, not independently verified values, market prices, portfolio performance or net-worth totals.
                    It is not a financial health score or a statement that your finances are complete.
                  </p>
                  {snapshot.coveredCategories < snapshot.coverage.length &&
                    capabilities.has('evidence.document.upload') && (
                      <a className="workspace-passport-review-link" href="/evidence">
                        Review documents for missing information
                      </a>
                    )}
                </div>
              )}
              {hasTrustedInformation ? (
                <div className="workspace-passport-panel">
                  <div className="workspace-passport-heading">
                    <div>
                      <p className="workspace-eyebrow">RECENT TRUSTED INFORMATION</p>
                      <h3>Follow each item back to its source</h3>
                    </div>
                    <span>{snapshot.sources} source {snapshot.sources === 1 ? 'document' : 'documents'}</span>
                  </div>
                  <div className="workspace-passport-list">
                    {snapshot.latest.map((fact) => (
                      <a
                        href={'/finances?fact=' + encodeURIComponent(fact.financialFactId)}
                        key={fact.financialFactId}
                      >
                        <span>
                          <strong>{fact.label}</strong>
                          <small>{fact.factType === 'INCOME' ? 'Income' : fact.factType === 'EXPENSE' ? 'Expense' : fact.factType === 'LOAN' ? 'Loan information' : fact.factType === 'ASSET' ? 'Asset source figure' : fact.factType === 'INVESTMENT' ? 'Investment source figure' : fact.factType === 'INSURANCE' ? 'Insurance source figure' : fact.factType}</small>
                        </span>
                        <span>
                          <small>Source</small>
                          <strong>{fact.source.sourceName}</strong>
                        </span>
                        <WorkspaceIcon name="arrow" size={16} />
                      </a>
                    ))}
                  </div>
                </div>
              ) : canReadFinancial ? (
                <div className="workspace-passport-panel workspace-passport-empty">
                  <WorkspaceIcon name="reports" size={22} />
                  <div>
                    <strong>Your Financial Passport is ready for trusted information.</strong>
                    <p>Review supported Income, Expense, Loan, Asset, Investment or Insurance source details from Documents and choose what to add.</p>
                  </div>
                  {capabilities.has('evidence.document.upload') && <a href="/evidence">Review documents</a>}
                </div>
              ) : null}
            </>
          )}
        </section>
      )}

      <section aria-labelledby="workspace-areas-title">
        <div className="workspace-section-heading">
          <div>
            <p className="workspace-eyebrow">AVAILABLE TO YOU</p>
            <h2 id="workspace-areas-title">Continue your financial journey</h2>
          </div>
          <span>
            {available.length} of {journeys.length} journeys available
          </span>
        </div>
        {available.length ? (
          <div className="workspace-area-grid">
            {available.map((area, index) => (
              <a className="workspace-area-card" href={area.href} key={area.title}>
                <div className="workspace-card-top">
                  <span className="workspace-card-icon">
                    <WorkspaceIcon name={area.icon} size={21} />
                  </span>
                  <span className="workspace-card-number">0{index + 1}</span>
                </div>
                <h3>{area.title}</h3>
                <p>{area.description}</p>
                <span className="workspace-card-action">
                  {area.action}
                  <WorkspaceIcon name="arrow" size={17} />
                </span>
              </a>
            ))}
          </div>
        ) : (
          <div className="workspace-access-empty">
            <WorkspaceIcon name="shield" size={23} />
            <div>
              <strong>Your workspace is connected, but no financial journeys are available.</strong>
              <p>Contact your Organization Administrator if you need additional access.</p>
            </div>
          </div>
        )}
      </section>

      <section className="workspace-boundary" aria-labelledby="workspace-boundary-title">
        <span className="workspace-card-icon">
          <WorkspaceIcon name="shield" size={20} />
        </span>
        <div>
          <p className="workspace-eyebrow">YOUR ACCESS</p>
          <h2 id="workspace-boundary-title">
            You are viewing {context?.customerName ?? 'your own financial workspace'}.
          </h2>
          <p>
            Monergy resolves this customer from your verified identity. You do not choose or enter
            an internal customer ID.
          </p>
        </div>
        <div className="workspace-boundary-facts">
          <span>
            <small>Environment</small>
            {context?.environment ?? 'Preview'}
          </span>
          <span>
            <small>Data</small>
            {context?.dataDurability ?? 'Temporary'}
          </span>
        </div>
      </section>
    </main>
  );
}
