import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
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
      throw new Error('Unexpected request: ' + url);
    }));
    const user = userEvent.setup();
    render(<PlatformAdministratorPortal />);
    const key = screen.getByLabelText('Platform Administrator key');
    await user.type(key, 'platform-key');
    await user.click(screen.getByRole('button', { name: 'Connect platform workspace' }));

    expect(await screen.findByRole('heading', { name: 'Platform health' })).toBeVisible();
    expect(screen.getByText(/No tenant membership, customer-data access/)).toBeVisible();
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
      throw new Error('Unexpected request: ' + url);
    }));
    const user = userEvent.setup();
    render(<PlatformAdministratorPortal />);
    await user.type(screen.getByLabelText('Platform Administrator key'), 'platform-key');
    await user.click(screen.getByRole('button', { name: 'Connect platform workspace' }));
    expect(await screen.findByText('PLATFORM_CONTEXT_INVALID')).toBeVisible();
    expect(screen.queryByRole('heading', { name: 'Platform health' })).not.toBeInTheDocument();
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
    await user.type(screen.getByLabelText('Platform Administrator key'), 'platform-key');
    await user.click(screen.getByRole('button', { name: 'Connect platform workspace' }));
    await user.click(await screen.findByRole('button', { name: 'Record posture' }));
    const dialog = within(await screen.findByRole('dialog'));
    await user.type(dialog.getByLabelText('Operational note'), 'Checking dependency.');
    await user.click(dialog.getByRole('button', { name: 'Record posture' }));

    await waitFor(() => { expect(writes).toHaveLength(1); });
    expect(writes[0]).toMatchObject({
      expectedVersion: 0, posture: 'Normal', note: 'Checking dependency.',
    });
    expect(writes[0]).not.toHaveProperty('tenantId');
    expect(writes[0]).not.toHaveProperty('permission');
    expect((await screen.findAllByText('Checking dependency.')).length).toBeGreaterThanOrEqual(1);
  });
});
