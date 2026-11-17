import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';

import PlatformAdministratorPortal from '../src/platform-administration/PlatformAdministratorPortal';

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
  it('uses a separate platform session and exposes no tenant or customer authority', async () => {
    const calls: { url: string; options?: RequestInit }[] = [];
    vi.stubGlobal('fetch', vi.fn((url: string, options?: RequestInit) => {
      calls.push({ url, options });
      if (url.endsWith('/sessions')) return Promise.resolve(json(session));
      if (url.endsWith('/context')) return Promise.resolve(json(context));
      if (url.endsWith('/topology')) return Promise.resolve(json(topology));
      if (url.endsWith('/readiness')) return Promise.resolve(json({ gates: [] }));
      if (url.endsWith('/audit')) return Promise.resolve(json({ items: [] }));
      if (url.endsWith('/integrations')) return Promise.resolve(json(integrations));
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
  });

  it('fails closed when the server reports tenant or break-glass authority', async () => {
    vi.stubGlobal('fetch', vi.fn((url: string) => {
      if (url.endsWith('/sessions')) return Promise.resolve(json(session));
      if (url.endsWith('/context')) return Promise.resolve(json({ ...context, tenantAccess: true }));
      if (url.endsWith('/topology')) return Promise.resolve(json(topology));
      if (url.endsWith('/readiness')) return Promise.resolve(json({ gates: [] }));
      if (url.endsWith('/audit')) return Promise.resolve(json({ items: [] }));
      if (url.endsWith('/integrations')) return Promise.resolve(json(integrations));
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
