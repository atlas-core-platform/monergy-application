import type { WorkspaceDestination } from '@monergy/ui-foundation';

export const destinations: WorkspaceDestination[] = [
  { id: 'home', label: 'Overview', icon: 'home', href: '/', group: 'Your finances' },
  { id: 'actions', label: 'Action Centre', icon: 'changes', href: '/actions' },
  { id: 'evidence', label: 'Documents', icon: 'evidence', href: '/evidence' },
  { id: 'net-worth', label: 'Net Worth', icon: 'reports', href: '/net-worth' },
  { id: 'finances', label: 'Financial information', icon: 'reports', href: '/finances' },
  { id: 'search', label: 'Search', icon: 'search', href: '/search' },
  { id: 'reports', label: 'Reports', icon: 'reports', href: '/reports' },
  { id: 'connections', label: 'Connections', icon: 'evidence', href: '/connections' },
  { id: 'consent', label: 'Consent & privacy', icon: 'check', href: '/consent' },
  { id: 'profile', label: 'My Profile', icon: 'shield', href: '/profile', group: 'Your account' },
  { id: 'agreements', label: 'Account agreements', icon: 'check', href: '/settings/agreements' },
  { id: 'sessions', label: 'Sessions', icon: 'shield', href: '/settings/sessions' },
];

export const productModule = {
  name: 'Financial Workspace',
  homeHref: '/',
  navigationLabel: 'Jump to a financial page',
  findLabel: 'Find a financial page',
  guideLabel: 'Financial workspace guide',
  guideTitle: 'Your financial workspace guide',
  guide: (
    <>
      <h2>Turn documents into clear financial information.</h2>
      <p>
        Start in Documents to add a financial record. Review extracted details before adding them
        to Financial information. Search and Reports only return information your current customer access allows.
      </p>
      <h3>Add documents</h3>
      <p>
        Drop or choose a supported file, review what will be uploaded, and wait for Monergy to
        confirm the document version.
      </p>
      <h3>Follow the source</h3>
      <p>
        Open Document details to see when a document was uploaded, review its Evidence-owned version
        history, and open an exact historical version when needed. Technical details retain immutable
        identifiers and integrity hashes. A confirmed upload does not change Financial information
        until you explicitly review and add supported details.
      </p>
      <h3>Track processing</h3>
      <p>
        Recent document-processing status remains visible in Documents after you close the review
        drawer or return later. For completed runs, you can reopen the extracted candidate
        information and resume the same explicit review-and-confirm flow. Items already represented
        by the exact source fact and document version are marked as trusted and link back to the
        current Financial fact; they cannot be selected again.
      </p>
      <h3>Know this environment</h3>
      <p>
        This is a test environment and its data may be reset. Production retention and release
        remain separately governed.
      </p>
    </>
  ),
};
