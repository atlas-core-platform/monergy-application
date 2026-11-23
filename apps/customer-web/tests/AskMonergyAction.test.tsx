import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterAll, afterEach, beforeAll, expect, it, vi } from 'vitest';
import type ActionType from '../src/assistant/AskMonergyAction';
import type { AssistantSession } from '../src/assistant/assistantApi';

let Action: typeof ActionType;
beforeAll(async () => {
  vi.stubEnv('VITE_MONERGY_ASSISTANT_ENABLED', 'true');
  Action = (await import('../src/assistant/AskMonergyAction')).default;
});
afterAll(() => { vi.unstubAllEnvs(); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
const session: AssistantSession = { audience: 'product', credential: 'synthetic-session', expiresAt: '2099-01-01T00:00:00Z' };
const ready = () => new Response(JSON.stringify({ enabled: true, available: true, reviewAllowed: false }));

it('loads only on demand, closes by keyboard and reopens without conversation state', async () => {
  const fetcher = vi.fn(() => Promise.resolve(ready())); vi.stubGlobal('fetch', fetcher);
  const end = vi.fn();
  render(<FoundationProvider appearance="midnight"><Action session={session} workspace="Customer" onSessionEnded={end} /></FoundationProvider>);
  expect(fetcher).not.toHaveBeenCalled();
  expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
  const user = userEvent.setup();
  await user.click(screen.getByRole('button', { name: 'Ask Monergy' }));
  fireEvent.change(await screen.findByRole('textbox', { name: 'Your question · Customer' }), { target: { value: 'memory-only-canary' } });
  await user.keyboard('{Escape}');
  await waitFor(() => { expect(screen.queryByDisplayValue('memory-only-canary')).not.toBeInTheDocument(); });
  await user.click(screen.getByRole('button', { name: 'Ask Monergy' }));
  expect(await screen.findByRole('textbox', { name: 'Your question · Customer' })).toHaveValue('');
  expect(end).not.toHaveBeenCalled();
});

it.each(['workspace', 'session', 'audience', 'expiry'] as const)('destroys temporary question text on %s boundary change', async boundary => {
  vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(ready())));
  const end = vi.fn();
  const element = (current: AssistantSession, workspace: string) => <FoundationProvider appearance="midnight"><Action session={current} workspace={workspace} onSessionEnded={end} /></FoundationProvider>;
  const view = render(element(session, 'Customer')); const user = userEvent.setup();
  await user.click(screen.getByRole('button', { name: 'Ask Monergy' }));
  fireEvent.change(await screen.findByRole('textbox', { name: 'Your question · Customer' }), { target: { value: 'discard-on-boundary' } });
  const changed = { ...session };
  if (boundary === 'session') changed.credential = 'new-synthetic-session';
  if (boundary === 'audience') changed.audience = 'platform';
  if (boundary === 'expiry') changed.expiresAt = '2098-01-01T00:00:00Z';
  view.rerender(element(changed, boundary === 'workspace' ? 'Organization Administration' : 'Customer'));
  await waitFor(() => { expect(screen.queryByDisplayValue('discard-on-boundary')).not.toBeInTheDocument(); });
  expect(await screen.findByRole('textbox')).toHaveValue('');
});
