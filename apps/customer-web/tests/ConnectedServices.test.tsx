import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ConnectedServices } from '../src/access/ConnectedServices';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('Connected Services', () => {
  it('shows only the administrator-scoped safe provider projection', async () => {
    const fetcher = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      expect(input).toBe('/uat-api/v1/connected-services');
      expect(init?.cache).toBe('no-store');
      return Promise.resolve(new Response(JSON.stringify({
        tenantId: 'T001', revision: 2,
        providers: [{ code: 'email', displayName: 'Email delivery', category: 'Communication',
          readiness: 'Configured for readiness check', subscriptionStatus: 'Configured',
          updatedAt: '2026-11-17T12:00:00Z' }],
      }), { status: 200, headers: { 'Content-Type': 'application/json' } }));
    });
    vi.stubGlobal('fetch', fetcher);
    render(<ConnectedServices session={{ tenantId: 'T001', actorId: 'A900', authenticationContextId: 'session-1',
      subjectVersion: 1, expiresAt: '2099-01-01T00:00:00Z' }} />);
    expect(await screen.findByText('Email delivery')).toBeVisible();
    expect(screen.getByText('Configured for readiness check')).toBeVisible();
    expect(screen.queryByText('smtp-primary')).not.toBeInTheDocument();
    expect(screen.queryByText('provider-secret-value')).not.toBeInTheDocument();
    const headers = new Headers(fetcher.mock.calls[0]?.[1]?.headers);
    expect(headers.get('X-Monergy-Tenant')).toBe('T001');
    expect(headers.get('X-Monergy-Session')).toBe('session-1');
  });

  it('fails closed if a response crosses the organization boundary', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response(JSON.stringify({ tenantId: 'T002', providers: [] }), { status: 200 }))));
    render(<ConnectedServices session={{ tenantId: 'T001', actorId: 'A900', authenticationContextId: 'session-1',
      subjectVersion: 1, expiresAt: '2099-01-01T00:00:00Z' }} />);
    expect(await screen.findByText('Connected services returned an invalid organization context.')).toBeVisible();
  });
});
