import { FoundationProvider } from '@monergy/ui-foundation';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import axe from 'axe-core';
import { afterEach, describe, expect, it } from 'vitest';

import { App } from '../src/App';

afterEach(cleanup);

describe('D02 toolchain shell', () => {
  it('preserves toolchain evidence and exposes accepted VS-02 plus authorized search', () => {
    render(
      <FoundationProvider>
        <App />
      </FoundationProvider>,
    );

    expect(screen.getByRole('heading', { name: 'Monergy frontend foundation' })).toBeVisible();
    expect(screen.getByText('ACCEPTED · SIMULATOR')).toBeVisible();
    expect(screen.getByRole('link', { name: 'Open VS-02 reference flow' })).toHaveAttribute(
      'href',
      '/vs02',
    );
    expect(screen.getByRole('link', { name: 'Open authorized search' })).toHaveAttribute(
      'href',
      '/search',
    );
  });

  it('provides keyboard-operable Ant Design interaction and restores focus', async () => {
    const user = userEvent.setup();
    render(
      <FoundationProvider>
        <App />
      </FoundationProvider>,
    );

    const trigger = screen.getByRole('button', { name: 'View toolchain evidence' });
    trigger.focus();
    await user.keyboard('{Enter}');
    const dialog = screen.getByRole('dialog', { name: 'D02 frontend evidence' });
    expect(dialog).toBeInTheDocument();
    const close = screen.getByRole('button', { name: 'Close evidence' });
    close.focus();
    await user.click(close);
    expect(screen.queryByRole('dialog', { name: 'D02 frontend evidence' })).not.toBeInTheDocument();
    await waitFor(() => {
      expect(trigger).toHaveFocus();
    });
  });

  it('has no detectable critical accessibility violations', async () => {
    const { container } = render(
      <FoundationProvider>
        <App />
      </FoundationProvider>,
    );

    const results = await axe.run(container, { resultTypes: ['violations'] });
    expect(results.violations).toHaveLength(0);
  });
});
