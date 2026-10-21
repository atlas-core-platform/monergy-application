import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { CustomerRegistrationEntry } from '../src/access/CustomerRegistrationEntry';
const json = (v: unknown, status = 200) => new Response(JSON.stringify(v), { status, headers: { 'Content-Type': 'application/json' } });
const publication = { revision: 'b'.repeat(64), documents: ['TERMS', 'PRIVACY', 'CONSENT'].map(kind => ({ kind, title: kind, version: 'test', body: 'Fixture publication', sha256: 'c'.repeat(64) })) };
const challenge = { challengeId: 'a'.repeat(64), expiresAt: new Date(Date.now() + 300000).toISOString(), retryAfterSeconds: 60 };
function mount() { const onSignIn = vi.fn().mockResolvedValue(undefined), onCancel = vi.fn(); render(<FoundationProvider appearance="midnight"><CustomerRegistrationEntry busy={false} onSignIn={onSignIn} onCancel={onCancel} /></FoundationProvider>); return { onSignIn, onCancel }; }
function owner() { const fetch = vi.fn((url: string, options?: RequestInit) => { if (options?.signal?.aborted) return Promise.reject(new Error("Aborted")); return Promise.resolve(json(url.includes('/agreements') ? publication : url.endsWith('/request') ? challenge : { tenantId: 'T001', actorId: 'ci-new', customerId: 'customer-new', active: true, recovered: true })); }); vi.stubGlobal('fetch', fetch); return fetch; }
async function request(mode = 'Create account') { await userEvent.click(screen.getByRole('button', { name: mode })); fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'new@example.test' } }); await userEvent.click(screen.getByRole('button', { name: 'Send verification code' })); return screen.findByLabelText('Verification code'); }
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });
it('requires all published agreements and binds confirmation to tenant and revision', async () => {
 const fetch = owner(); const { onSignIn } = mount(); const code = await request();
 fireEvent.change(code, { target: { value: '123456' } }); fireEvent.change(screen.getByLabelText('Your name'), { target: { value: 'New customer' } }); fireEvent.change(screen.getByLabelText('Create a password (15–128 characters)'), { target: { value: 'a long password value' } });
 await userEvent.click(screen.getByRole('button', { name: 'Verify and create account' })); expect(fetch.mock.calls.some(([u]) => u.endsWith('/confirm'))).toBe(false);
 for (const box of screen.getAllByRole('checkbox')) await userEvent.click(box);
 await userEvent.click(screen.getByRole('button', { name: 'Verify and create account' })); expect(await screen.findByText(/Your account is ready/)).toBeVisible();
 const call = fetch.mock.calls.find(([u]) => u.endsWith('/confirm')); expect(JSON.parse(typeof call?.[1]?.body === 'string' ? call[1].body : '{}')).toMatchObject({ tenantId: 'T001', challengeId: challenge.challengeId, agreementRevision: publication.revision, acceptTerms: true, acknowledgePrivacy: true, consentToProcessing: true }); expect(onSignIn).not.toHaveBeenCalled(); expect(screen.getByLabelText('Password')).toHaveValue('');
});
it('recovers credentials without issuing a session', async () => {
 const fetch = owner(); const { onSignIn } = mount(); const code = await request('Forgot password'); fireEvent.change(code, { target: { value: '123456' } }); fireEvent.change(screen.getByLabelText('Create a password (15–128 characters)'), { target: { value: 'another long password' } }); await userEvent.click(screen.getByRole('button', { name: 'Verify and reset password' })); expect(await screen.findByText(/previous sessions have ended/)).toBeVisible(); expect(fetch.mock.calls.some(([u]) => u.endsWith('/recovery/confirm'))).toBe(true); expect(onSignIn).not.toHaveBeenCalled();
});
it('clears credentials before sign-in without persistence', async () => {
 const storage = vi.spyOn(Storage.prototype, 'setItem'); const { onSignIn } = mount(); fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'new@example.test' } }); fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'a long password value' } }); await userEvent.click(screen.getByRole('button', { name: /^Sign in$/ })); await waitFor(() => { expect(onSignIn).toHaveBeenCalledWith('T001', 'new@example.test', 'a long password value'); }); expect(screen.getByLabelText('Password')).toHaveValue(''); expect(storage).not.toHaveBeenCalled();
});
it('fails closed on missing publications and conceals server details', async () => {
 vi.stubGlobal('fetch', vi.fn().mockResolvedValue(json({ error: 'secret server detail' }, 503))); mount(); await userEvent.click(screen.getByRole('button', { name: 'Create account' })); fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'new@example.test' } }); await userEvent.click(screen.getByRole('button', { name: 'Send verification code' })); expect(await screen.findByText(/Unable to complete this step/)).toBeVisible(); expect(screen.queryByText(/secret server detail/)).not.toBeInTheDocument(); expect(screen.queryByLabelText('Verification code')).not.toBeInTheDocument();
});

it('does not announce password recovery from a malformed owner receipt', async () => {
 const fetch = owner(); const fallback = fetch.getMockImplementation(); if (!fallback) throw new Error('Missing mock'); fetch.mockImplementation((url, options) => url.endsWith('/recovery/confirm') ? Promise.resolve(json({ recovered: false })) : fallback(url, options));
 mount(); const code = await request('Forgot password'); fireEvent.change(code, { target: { value: '123456' } }); fireEvent.change(screen.getByLabelText('Create a password (15–128 characters)'), { target: { value: 'another long password' } });
 await userEvent.click(screen.getByRole('button', { name: 'Verify and reset password' })); expect(await screen.findByText(/Unable to complete this step/)).toBeVisible(); expect(screen.queryByText(/previous sessions have ended/)).not.toBeInTheDocument();
});
