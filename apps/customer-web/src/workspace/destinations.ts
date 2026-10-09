import type { WorkspaceDestination } from '@monergy/ui-foundation';

export const destinations: WorkspaceDestination[] = [
  { id: 'home', label: 'Overview', icon: 'home', href: '/', group: 'Workspace' },
  { id: 'vs02', label: 'Evidence', icon: 'evidence', href: '/vs02', group: 'Workspace' },
  { id: 'search', label: 'Search', icon: 'search', href: '/search', group: 'Workspace' },
  { id: 'reports', label: 'Reports', icon: 'reports', href: '/reports', group: 'Workspace' },
  {
    id: 'access',
    label: 'Access management',
    icon: 'access',
    href: '/access',
    group: 'Administration',
  },
  {
    id: 'expert',
    label: 'System Expert',
    icon: 'expert',
    href: 'http://127.0.0.1:4310/',
    group: 'Intelligence',
  },
  {
    id: 'operations',
    label: 'Local UAT operations',
    icon: 'workflow',
    href: '/operations',
    group: 'Operations',
  },
  {
    id: 'foundation',
    label: 'Engineering foundation',
    icon: 'layers',
    href: '/foundation',
    group: 'Intelligence',
  },
];
