import { FoundationProvider } from '@monergy/ui-foundation';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import axe from 'axe-core';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import Vs02Experience from '../src/vs02/Vs02Experience';
import { finishComponentClock, startComponentClock } from './componentLifecycle';

const completedResult = {
  runId: 'run-001',
  processingState: 'COMPLETED',
  evidenceId: 'evidence-001',
  documentVersionId: 'document-version-001',
  observations: [
    {
      sourceFactId: 'source-fact-001',
      label: 'Salary',
      candidateValue: 125000,
      currency: 'INR',
      confidence: 0.98,
      sourceLocation: 'page:1',
    },
  ],
  financialFacts: [
    {
      financialFactId: 'financial-fact-001',
      label: 'Salary',
      value: 125000,
      currency: 'INR',
      revision: 1,
      financialProvenanceId: 'provenance-001',
    },
  ],
  auditRecordCount: 5,
  correlationId: 'correlation-001',
} as const;

function renderExperience() {
  return render(
    <FoundationProvider>
      <Vs02Experience />
    </FoundationProvider>,
  );
}

beforeEach(startComponentClock);

afterEach(async () => {
  try {
    await finishComponentClock();
  } finally {
    vi.unstubAllGlobals();
  }
});

describe('VS-02 evidence-to-financial-truth experience', () => {
  it('clearly identifies reference evidence and its initial empty state', () => {
    renderExperience();

    expect(screen.getByText('REFERENCE · LOCAL / CI ONLY')).toBeVisible();
    expect(screen.getByText('VS-02 IMPLEMENTATION CANDIDATE')).toBeVisible();
    expect(screen.getByText(/Run the reference flow to inspect/)).toBeVisible();
    expect(screen.queryByText('AUTHORITATIVE')).not.toBeInTheDocument();
  });

  it('announces success and separates observations from authoritative facts', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify(completedResult), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
    );
    vi.stubGlobal('fetch', fetchMock);
    renderExperience();

    await user.click(screen.getByRole('button', { name: 'Run reference flow' }));

    await waitFor(() => {
      expect(screen.getByRole('status')).toHaveTextContent('Reference flow completed.');
    });
    expect(screen.getByText('NOT AUTHORITATIVE FINANCIAL TRUTH')).toBeVisible();
    expect(screen.getByText('AUTHORITATIVE')).toBeVisible();
    expect(screen.getAllByText('Salary')).toHaveLength(2);
    expect(screen.getByText('provenance-001')).toBeVisible();
    expect(fetchMock).toHaveBeenCalledWith(
      '/local-ci/vs02/reference-runs',
      expect.objectContaining({ method: 'POST' }),
    );
  });

  it('surfaces classified reference failures without presenting stale success', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response(
          JSON.stringify({
            code: 'processing.evidence.unavailable',
            message: 'Evidence content is unavailable.',
            retryable: true,
          }),
          { status: 503, headers: { 'content-type': 'application/json' } },
        ),
      ),
    );
    renderExperience();

    await user.click(screen.getByRole('button', { name: 'Run reference flow' }));

    expect(await screen.findByText('Reference flow failed')).toBeVisible();
    expect(screen.getByText(/processing\.evidence\.unavailable/)).toBeVisible();
    expect(screen.queryByText('AUTHORITATIVE')).not.toBeInTheDocument();
  });

  it('provides accessible validation feedback and no detectable critical violations', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    const { container } = renderExperience();
    await user.clear(screen.getByLabelText('Customer ID'));
    await user.click(screen.getByRole('button', { name: 'Run reference flow' }));

    expect(await screen.findByText('Enter a customer ID.')).toBeVisible();
    const results = await axe.run(container, { resultTypes: ['violations'] });
    expect(results.violations).toHaveLength(0);
  });
});
