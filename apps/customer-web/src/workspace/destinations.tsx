import type { WorkspaceDestination } from '@monergy/ui-foundation';

export const destinations: WorkspaceDestination[] = [
  { id: 'home', label: 'Overview', icon: 'home', href: '/', group: 'Your finances' },
  { id: 'evidence', label: 'Documents', icon: 'evidence', href: '/evidence' },
  { id: 'finances', label: 'Financial information', icon: 'reports', href: '/finances' },
  { id: 'search', label: 'Search', icon: 'search', href: '/search' },
  { id: 'reports', label: 'Reports', icon: 'reports', href: '/reports' },
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
        Open Document details to see when a document was uploaded and, when needed, expand Technical
        details for its immutable identifiers and integrity hash. A confirmed upload does not change
        Financial information until you explicitly review and add supported details.
      </p>
      <h3>Know this environment</h3>
      <p>
        This is a test environment and its data may be reset. Production retention and release
        remain separately governed.
      </p>
    </>
  ),
};
