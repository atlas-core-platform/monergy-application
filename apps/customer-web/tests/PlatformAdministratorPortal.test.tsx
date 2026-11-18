import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';

import PlatformAdministratorPortal from '../src/platform-administration/PlatformAdministratorPortal';
import { prepareAdministratorWelcomeUrl } from '../src/platform-administration/administratorWelcome';

const json = (value: unknown, status = 200) =>
  new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
const session = {
  actorId: 'A950', authority: 'PlatformAdministrator', session: 'platform-session',
  expiresAt: '2099-01-01T00:00:00Z',
};
const context = {
  actorId: 'A950', authority: 'PlatformAdministrator', environment: 'Local UAT',
  tenantAccess: false, customerDataAccess: false, breakGlassAvailable: false,
  productionAccepted: false,
};
const topology = {
  operationsRevision: 0,
  postures: [],
  services: [{ id: 'reporting', label: 'Reporting', implementation: 'Reports', available: true }],
};
const integrations = { revision: 0, providers: [{ code: 'email', displayName: 'Email delivery', category: 'Communication', enabled: false, scope: 'Platform', options: {}, secretConfigured: false, subscriptionStatus: 'Not configured', readiness: 'Not configured', updatedAt: '1970-01-01T00:00:00Z' }] };

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('Platform Administrator portal', () => {
  it('binds the first-administrator welcome page to the tenant host and invited recipient', () => {
    const welcome = prepareAdministratorWelcomeUrl({
      welcomeUrl: 'http://northwind.localhost:4338/welcome#tenant=tenant-02a&ticket=' +
        'a'.repeat(64) + '&return=%2Faccess',
      expiresAt: '2099-01-01T00:00:00Z',
      email: 'admin@northwind.example.test',
    }, {
      organizationCode: 'tenant-02a', displayName: 'Northwind',
      hostname: 'northwind.localhost', initialAdministrator: 'admin@northwind.example.test',
    }, { protocol: 'http:', port: '4338' });

    const invitation = new URLSearchParams(welcome.hash.slice(1));
    expect(welcome.origin).toBe('http://northwind.localhost:4338');
    expect(invitation.get('tenant')).toBe('tenant-02a');
    expect(invitation.get('organization')).toBe('Northwind');
    expect(invitation.get('email')).toBe('admin@northwind.example.test');
    expect(welcome.href).not.toContain('tester');
    expect(welcome.href).not.toContain('session');

    expect(() => prepareAdministratorWelcomeUrl({
      welcomeUrl: 'http://other.localhost:4338/welcome#tenant=tenant-02a&ticket=' +
        'a'.repeat(64) + '&return=%2Faccess',
      expiresAt: '2099-01-01T00:00:00Z', email: 'admin@northwind.example.test',
    }, {
      organizationCode: 'tenant-02a', displayName: 'Northwind',
      hostname: 'northwind.localhost', initialAdministrator: 'admin@northwind.example.test',
    }, { protocol: 'http:', port: '4338' })).toThrow('The administrator setup page was not valid.');

    expect(() => prepareAdministratorWelcomeUrl({
      welcomeUrl: 'http://northwind.localhost:4338/welcome#tenant=tenant-02a&ticket=' +
        'a'.repeat(64) + '&return=%2Faccess',
      expiresAt: '2099-01-01T00:00:00Z', email: 'someone-else@northwind.example.test',
    }, {
      organizationCode: 'tenant-02a', displayName: 'Northwind',
      hostname: 'northwind.localhost', initialAdministrator: 'admin@northwind.example.test',
    }, { protocol: 'http:', port: '4338' })).toThrow('The administrator setup page was not valid.');
  });

  it('creates an organization through the platform session without collecting an administrator password', async () => {
    let created = false;
    const writes: Record<string, unknown>[] = [];
    const organization = {
      organizationCode: 'tenant-02a', organizationName: 'Northwind Advisory',
      organizationId: 'org-northwind', displayName: 'Northwind', tenantSlug: 'northwind',
      hostname: 'northwind.localhost', tenantUrl: 'http://northwind.localhost:4338', endpointStatus: 'Registered',
      country: 'IN', timeZone: 'Asia/Calcutta',
      initialAdministrator: 'admin@northwind.example.test', status: 'ReadyForAdmin',
      created: '2026-11-18T10:00:00Z', updated: '2026-11-18T10:01:00Z',
      readiness: 'Ready for first administrator', completedSteps: [1, 7, 2, 3, 4, 5, 6],
    };
    vi.stubGlobal('fetch', vi.fn((url: string, options?: RequestInit) => {
      if (url.endsWith('/sessions')) return Promise.resolve(json(session));
      if (url.endsWith('/context')) return Promise.resolve(json(context));
      if (url.endsWith('/topology')) return Promise.resolve(json(topology));
      if (url.endsWith('/readiness')) return Promise.resolve(json({ gates: [] }));
      if (url.endsWith('/audit')) return Promise.resolve(json({ items: [] }));
      if (url.endsWith('/integrations')) return Promise.resolve(json(integrations));
      if (url.endsWith('/organizations') && options?.method === 'POST') {
        if (typeof options.body !== 'string') throw new Error('Expected organization JSON.');
        writes.push(JSON.parse(options.body) as Record<string, unknown>);
        created = true;
        return Promise.resolve(json({ organizationCode: organization.organizationCode,
          status: organization.status, completedSteps: organization.completedSteps }));
      }
      if (url.endsWith('/organizations'))
        return Promise.resolve(json({ items: created ? [organization] : [] }));
      throw new Error('Unexpected request: ' + url);
    }));
    render(<PlatformAdministratorPortal />);
    fireEvent.change(screen.getByLabelText('Password'),
      { target: { value: 'Platform!Password123' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    fireEvent.click(await screen.findByText('Organizations', { selector: 'span' }));
    fireEvent.click(screen.getByRole('button', { name: 'Create organization' }));
    const dialog = within(await screen.findByRole('dialog'));
    fireEvent.change(dialog.getByLabelText('Organization name'),
      { target: { value: 'Northwind Advisory' } });
    fireEvent.change(dialog.getByLabelText('Display name'),
      { target: { value: 'Northwind' } });
    fireEvent.change(dialog.getByLabelText('Organization URL name (optional)'),
      { target: { value: 'northwind' } });
    fireEvent.change(dialog.getByLabelText('Initial Organization Administrator email'),
      { target: { value: 'admin@northwind.example.test' } });
    expect(dialog.queryByLabelText(/password/i)).toBeNull();
    fireEvent.click(dialog.getByRole('button', { name: 'Create organization' }));
    expect(await screen.findByText(/Organization code: tenant-02a/)).toBeVisible();
    expect(writes).toHaveLength(1);
    expect(writes[0]).toMatchObject({ organizationName: 'Northwind Advisory',
      displayName: 'Northwind', initialAdministratorEmail: 'admin@northwind.example.test',
      tenantSlug: 'northwind', placement: 'Shared' });
    expect(screen.getByRole('link', { name: 'northwind.localhost' })).toHaveAttribute('href', 'http://northwind.localhost:4338');
    expect(JSON.stringify(writes[0])).not.toMatch(/password|tester|secret/i);
  });

  it('uses a separate platform session and exposes no tenant or customer authority', async () => {
    const calls: { url: string; options?: RequestInit }[] = [];
    vi.stubGlobal('fetch', vi.fn((url: string, options?: RequestInit) => {
      calls.push({ url, options });
      if (url.endsWith('/sessions/current') && options?.method === 'DELETE')
        return Promise.resolve(json({ revoked: true }));
      if (url.endsWith('/sessions')) return Promise.resolve(json(session));
      if (url.endsWith('/context')) return Promise.resolve(json(context));
      if (url.endsWith('/topology')) return Promise.resolve(json(topology));
      if (url.endsWith('/readiness')) return Promise.resolve(json({ gates: [] }));
      if (url.endsWith('/audit')) return Promise.resolve(json({ items: [] }));
      if (url.endsWith('/integrations')) return Promise.resolve(json(integrations));
      if (url.endsWith('/organizations')) return Promise.resolve(json({ items: [] }));
      throw new Error('Unexpected request: ' + url);
    }));
    const user = userEvent.setup();
    render(<PlatformAdministratorPortal />);
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'Platform!Password123' } });
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByRole('heading', { name: 'System status' })).toBeVisible();
    expect(screen.getByText(/cannot open organization or customer information/)).toBeVisible();
    expect(screen.queryByText('Organization Settings')).not.toBeInTheDocument();
    expect(screen.queryByText('Customer Assignments')).not.toBeInTheDocument();
    const contextCall = calls.find(call => call.url.endsWith('/context'));
    const headers = new Headers(contextCall?.options?.headers);
    expect(headers.get('X-Monergy-Platform-Session')).toBe('platform-session');
    expect(headers.has('X-Monergy-Tenant')).toBe(false);
    expect(headers.has('X-Monergy-Session')).toBe(false);
    await user.click(screen.getByRole('button', { name: 'Sign out' }));
    expect(await screen.findByText('Open Monergy Administration', { exact: true })).toBeVisible();
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'Platform!Password123' } });
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(await screen.findByRole('heading', { name: 'System status' })).toBeVisible();
    expect(calls.filter(call => call.url.endsWith('/sessions'))).toHaveLength(2);
  });

  it('fails closed when the server reports tenant or break-glass authority', async () => {
    vi.stubGlobal('fetch', vi.fn((url: string) => {
      if (url.endsWith('/sessions')) return Promise.resolve(json(session));
      if (url.endsWith('/context')) return Promise.resolve(json({ ...context, tenantAccess: true }));
      if (url.endsWith('/topology')) return Promise.resolve(json(topology));
      if (url.endsWith('/readiness')) return Promise.resolve(json({ gates: [] }));
      if (url.endsWith('/audit')) return Promise.resolve(json({ items: [] }));
      if (url.endsWith('/integrations')) return Promise.resolve(json(integrations));
      if (url.endsWith('/organizations')) return Promise.resolve(json({ items: [] }));
      throw new Error('Unexpected request: ' + url);
    }));
    const user = userEvent.setup();
    render(<PlatformAdministratorPortal />);
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'Platform!Password123' } });
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(await screen.findByText('Your administrator access could not be confirmed. Sign in again.')).toBeVisible();
    expect(screen.queryByRole('heading', { name: 'System status' })).not.toBeInTheDocument();
  });

  it('records only a bounded versioned operational posture', async () => {
    let changed = false;
    const writes: unknown[] = [];
    vi.stubGlobal('fetch', vi.fn((url: string, options?: RequestInit) => {
      if (url.endsWith('/sessions')) return Promise.resolve(json(session));
      if (url.endsWith('/context')) return Promise.resolve(json(context));
      if (url.endsWith('/topology')) return Promise.resolve(json(changed ? {
        ...topology, operationsRevision: 1,
        postures: [{ serviceId: 'reporting', posture: 'Normal', note: 'Checking dependency.', changedBy: 'A950', changedAt: '2026-11-16T12:00:00Z', version: 1 }],
      } : topology));
      if (url.endsWith('/readiness')) return Promise.resolve(json({ gates: [] }));
      if (url.endsWith('/audit')) return Promise.resolve(json({ items: changed ? [{ operationId: 'a'.repeat(32), serviceId: 'reporting', posture: 'Normal', note: 'Checking dependency.', actorId: 'A950', recordedAt: '2026-11-16T12:00:00Z', version: 1 }] : [] }));
      if (url.endsWith('/integrations')) return Promise.resolve(json(integrations));
      if (url.endsWith('/organizations')) return Promise.resolve(json({ items: [] }));
      if (url.includes('/posture')) {
        if (typeof options?.body !== 'string') throw new Error('Expected a JSON request body.');
        writes.push(JSON.parse(options.body));
        changed = true;
        return Promise.resolve(json({ version: 1 }));
      }
      throw new Error('Unexpected request: ' + url);
    }));
    const user = userEvent.setup();
    render(<PlatformAdministratorPortal />);
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'Platform!Password123' } });
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    await user.click(await screen.findByRole('button', { name: 'Add note' }));
    const dialog = within(await screen.findByRole('dialog'));
    await user.type(dialog.getByLabelText('Operational note'), 'Checking dependency.');
    await user.click(dialog.getByRole('button', { name: 'Save note' }));

    await waitFor(() => { expect(writes).toHaveLength(1); });
    expect(writes[0]).toMatchObject({
      expectedVersion: 0, posture: 'Normal', note: 'Checking dependency.',
    });
    expect(writes[0]).not.toHaveProperty('tenantId');
    expect(writes[0]).not.toHaveProperty('permission');
    expect((await screen.findAllByText('Checking dependency.')).length).toBeGreaterThanOrEqual(1);
  });

  it('submits a provider secret once and never receives or repopulates it', async () => {
    const writes: Record<string, unknown>[] = [];
    vi.stubGlobal('fetch', vi.fn((url: string, options?: RequestInit) => {
      if (url.endsWith('/sessions')) return Promise.resolve(json(session));
      if (url.endsWith('/context')) return Promise.resolve(json(context));
      if (url.endsWith('/topology')) return Promise.resolve(json(topology));
      if (url.endsWith('/readiness')) return Promise.resolve(json({ gates: [] }));
      if (url.endsWith('/audit')) return Promise.resolve(json({ items: [] }));
      if (url.endsWith('/integrations')) return Promise.resolve(json(integrations));
      if (url.endsWith('/organizations')) return Promise.resolve(json({ items: [] }));
      if (url.endsWith('/integrations/email')) {
        if (typeof options?.body !== 'string') throw new Error('Expected provider JSON.');
        writes.push(JSON.parse(options.body) as Record<string, unknown>);
        return Promise.resolve(json({ ...integrations, revision: 1 }));
      }
      throw new Error('Unexpected request: ' + url);
    }));
    render(<PlatformAdministratorPortal />);
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'Platform!Password123' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Configure' }));
    const dialog = within(await screen.findByRole('dialog'));
    fireEvent.change(dialog.getByLabelText('Set or replace secret'), { target: { value: 'local-smtp-secret' } });
    fireEvent.click(dialog.getByRole('button', { name: 'Save configuration' }));
    await waitFor(() => { expect(writes).toHaveLength(1); });
    expect(writes[0]).toMatchObject({ expectedRevision: 0, secretValue: 'local-smtp-secret', clearSecret: false });
    expect(JSON.stringify(integrations)).not.toContain('local-smtp-secret');
    await waitFor(() => { expect(screen.queryByRole('dialog')).not.toBeInTheDocument(); });
    fireEvent.click(await screen.findByRole('button', { name: 'Configure' }));
    expect(within(await screen.findByRole('dialog')).getByLabelText('Set or replace secret')).toHaveValue('');
  });
});
