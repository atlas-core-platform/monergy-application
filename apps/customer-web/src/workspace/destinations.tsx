import type { WorkspaceDestination } from '@monergy/ui-foundation';

export const destinations: WorkspaceDestination[] = [
  { id: 'home', label: 'Overview', icon: 'home', href: '/', group: 'Your finances' },
  { id: 'evidence', label: 'Evidence', icon: 'evidence', href: '/evidence' },
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
      <h2>Turn source documents into clear financial information.</h2>
      <p>
        Start in Evidence to add a financial document. Search and Reports only return information
        your current customer access allows.
      </p>
      <h3>Add evidence</h3>
      <p>
        Drop or choose a supported file, review exactly what will be registered, and wait for a
        confirmation from the Evidence service.
      </p>
      <h3>Follow the source</h3>
      <p>
        Evidence identities, document versions and source hashes make later financial information
        traceable. A confirmed upload does not imply extraction or financial advice.
      </p>
      <h3>Know this environment</h3>
      <p>
        The current customer application is a LOCAL/UAT slice. Uploaded content is retained only for
        the running reference environment; Production retention and release remain separate.
      </p>
    </>
  ),
};
