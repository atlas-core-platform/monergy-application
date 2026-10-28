import type { WorkspaceDestination } from '@monergy/ui-foundation';

export const accessDestinations: WorkspaceDestination[] = [
  { id: 'audit', label: 'Audit Log', icon: 'changes', href: '/access/audit', group: 'Operations' },
  { id: 'overview', label: 'Overview', icon: 'home', href: '/access', group: 'Organization' },
  { id: 'documents', label: 'Document Monitoring', icon: 'changes', href: '/access/documents', group: 'Operations' },
  { id: 'processing', label: 'Processing Status', icon: 'changes', href: '/access/processing', group: 'Operations' },
  { id: 'customers', label: 'Customers', icon: 'users', href: '/access/customers' },
  { id: 'requests', label: 'Access Requests', icon: 'check', href: '/access/requests' },
  { id: 'users', label: 'People', icon: 'users', href: '/access/users' },
  { id: 'roles', label: 'Roles', icon: 'shield', href: '/access/roles' },
  { id: 'permissions', label: 'Access Permissions', icon: 'check', href: '/access/permissions' },
  { id: 'groups', label: 'Teams', icon: 'layers', href: '/access/groups' },
  { id: 'resources', label: 'Customer Assignments', icon: 'map', href: '/access/resources' },
  {
    id: 'activity',
    label: 'Access Activity',
    icon: 'changes',
    href: '/access/activity',
    group: 'Security & oversight',
  },
  { id: 'sessions', label: 'Security & Sessions', icon: 'access', href: '/access/sessions' },
];
export const accessModule = {
  name: 'Organization Settings',
  homeHref: '/access',
  guide: (
    <>
      <h2>Give the right people the right access.</h2>
      <p>
        Create people, assign a business role, and review access changes within your organization.
      </p>
      <h3>Start with People</h3>
      <p>
        Create one person or import a team. Open a person to change their role, status or
        Organization Administrator authority. Onboarding history shows incomplete requests and
        recovery options.
      </p>
      <h3>Build access with roles</h3>
      <p>
        Access Permissions describe allowed actions. A role combines permissions, and each person
        can have one business role. Customer Assignments connect authorized professionals to named
        customers when the workflow requires that relationship.
      </p>
      <h3>Keep organization separate from access</h3>
      <p>
        Teams organize people; joining a team does not grant access. Organization Administrator
        authority manages access settings and does not itself grant financial access.
      </p>
      <h3>Review before you save</h3>
      <p>
        Changes show a summary before they are applied. Access Activity records changes. Security &
        Sessions lets you revoke all sessions for a person.
      </p>
      <h3>Navigate your way</h3>
      <p>
        Use Ctrl K or Command K to find a page. Collapse the sidebar for more room. Animation
        respects your device’s reduced-motion setting.
      </p>
    </>
  ),
};
