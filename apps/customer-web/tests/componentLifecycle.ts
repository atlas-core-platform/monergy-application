import { act, cleanup } from '@testing-library/react';
import { expect, vi } from 'vitest';

// This test owns the clock from before mount until every queued callback has
// executed after React cleanup. Never discard timers to make teardown pass.
export function startComponentClock() {
  vi.useFakeTimers({ shouldAdvanceTime: true });
}

export async function finishComponentClock() {
  cleanup();
  try {
    await act(async () => {
      await vi.runOnlyPendingTimersAsync();
    });
    expect(vi.getTimerCount(), 'Component callbacks must finish before jsdom teardown').toBe(0);
    expect(document.body.childElementCount, 'React roots and portals must be removed').toBe(0);
  } finally {
    vi.useRealTimers();
  }
  if (process.env.MONERGY_D05_LIFECYCLE_PROBE === '1') {
    console.info(
      'D05_LIFECYCLE_CLEANUP',
      JSON.stringify({
        test: expect.getState().currentTestName,
        pendingTimers: 0,
        remainingRoots: 0,
        realTimersRestored: !vi.isFakeTimers(),
      }),
    );
  }
}
