import type { WorkspaceDestination } from '@monergy/ui-foundation';

export const accessDestinations: WorkspaceDestination[] = [
  { id: 'overview', label: 'Overview', icon: 'home', href: '/access', group: 'Administration' },
  { id: 'users', label: 'People', icon: 'users', href: '/access/users' },
  { id: 'roles', label: 'Roles', icon: 'shield', href: '/access/roles' },
  { id: 'permissions', label: 'Access Permissions', icon: 'check', href: '/access/permissions' },
  { id: 'groups', label: 'Teams', icon: 'layers', href: '/access/groups' },
  { id: 'resources', label: 'Customer Access', icon: 'map', href: '/access/resources' },
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
  name: 'Organization & Access',
  homeHref: '/access',
  guide: (
    <>
      <h2>Give the right people the right access.</h2>
      <p>Create people, assign a business role, and review access changes within your organization.</p>
      <h3>Start with People</h3>
      <p>
        Add one person or import a team. Open a person to change their role, status or Organization
        Administrator authority. Onboarding history shows incomplete requests and recovery options.
      </p>
      <h3>Build access with roles</h3>
      <p>
        Access Permissions describe allowed actions. A role combines those permissions, and each
        person can have one business role. Customer Access connects people to named customers where
        a workflow requires that relationship.
      </p>
      <h3>Keep organization separate</h3>
      <p>
        Teams organize people; they do not grant access. Organization Administrator authority
        controls administration and does not itself grant business permissions.
      </p>
      <h3>Review before you save</h3>
      <p>
        Changes show a summary before they are applied. Access Activity records changes. Security &
        Sessions lets you revoke all sessions for a person.
      </p>
    </>
  ),
};
