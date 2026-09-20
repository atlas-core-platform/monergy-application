import { cleanup, act, render } from '@testing-library/react';
import { Form, Input } from 'antd';
import { expect, it, vi } from 'vitest';
import { finishComponentClock, startComponentClock } from './componentLifecycle';

it('isolates a Form.Item debounce callback surviving React cleanup', async () => {
  vi.useFakeTimers();
  const schedule = globalThis.setTimeout;
  let unmounted = false;
  const callbacks: { delay: number | undefined; stack: string; afterUnmount: boolean }[] = [];
  const timerSpy = vi
    .spyOn(globalThis, 'setTimeout')
    .mockImplementation((handler, delay, ...args) => {
      const record = { delay, stack: new Error('scheduled here').stack ?? '', afterUnmount: false };
      callbacks.push(record);
      return schedule(() => {
        record.afterUnmount = unmounted;
        if (typeof handler !== 'function') throw new Error('Unexpected string timer');
        handler(...args);
      }, delay);
    });
  try {
    render(
      <Form>
        <Form.Item label="Customer ID" name="customerId">
          <Input />
        </Form.Item>
      </Form>,
    );
    const before = vi.getTimerCount();
    cleanup();
    unmounted = true;
    expect(document.body.childElementCount).toBe(0);
    const after = vi.getTimerCount();
    expect(after).toBeGreaterThan(0);
    await act(async () => {
      await vi.runAllTimersAsync();
    });
    const escaped = callbacks.filter(
      (record) => record.afterUnmount && record.stack.includes('useDelayState'),
    );
    expect(escaped.length).toBeGreaterThan(0);
    expect(
      escaped.every((record) => record.delay === 10 && record.stack.includes('useDebounce')),
    ).toBe(true);
    expect(vi.getTimerCount()).toBe(0);
    console.info(
      JSON.stringify(
        { before, afterCleanup: after, afterDrain: vi.getTimerCount(), escaped },
        null,
        2,
      ),
    );
  } finally {
    cleanup();
    timerSpy.mockRestore();
    vi.useRealTimers();
  }
});

it('executes rather than discards the test-owned callbacks before releasing jsdom', async () => {
  startComponentClock();
  render(
    <Form>
      <Form.Item label="Customer ID" name="customerId">
        <Input />
      </Form.Item>
    </Form>,
  );
  let executed = false;
  setTimeout(() => {
    executed = true;
  }, 10);
  await finishComponentClock();
  expect(executed).toBe(true);
  expect(vi.isFakeTimers()).toBe(false);
  expect(document.body.childElementCount).toBe(0);
});

it('distinguishes delivered Form watcher messages from retained native ports', async () => {
  const NativeChannel = globalThis.MessageChannel;
  const channels: { channel: MessageChannel; delivered: boolean; completion: Promise<void> }[] = [];
  class ObservedChannel extends NativeChannel {
    constructor() {
      super();
      const record = { channel: this, delivered: false, completion: Promise.resolve() };
      record.completion = new Promise<void>((resolve) => {
        this.port1.addEventListener(
          'message',
          () => {
            record.delivered = true;
            resolve();
          },
          { once: true },
        );
      });
      channels.push(record);
    }
  }
  vi.stubGlobal('MessageChannel', ObservedChannel);
  try {
    startComponentClock();
    render(
      <Form>
        <Form.Item label="Customer ID" name="customerId">
          <Input />
        </Form.Item>
      </Form>,
    );
    await finishComponentClock();
    const pendingAfterFinish = channels.filter((entry) => !entry.delivered).length;
    await act(async () => {
      await Promise.all(channels.map((entry) => entry.completion));
    });
    expect(channels.length).toBeGreaterThan(0);
    expect(channels.every((entry) => entry.delivered)).toBe(true);
    console.info(
      JSON.stringify({ watcherChannels: channels.length, pendingAfterFinish, pendingMessages: 0 }),
    );
    expect(pendingAfterFinish).toBe(0);
  } finally {
    // These are diagnostic-owned native channels, closed only AFTER delivery.
    for (const { channel } of channels) {
      channel.port1.close();
      channel.port2.close();
    }
    vi.unstubAllGlobals();
  }
});
