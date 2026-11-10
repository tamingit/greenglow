import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { UserFeed } from '../src/userfeed.ts';

/** A stand-in WebSocket: the test opens, messages and closes it. */
class FakeSocket extends EventTarget {
  static made: FakeSocket[] = [];
  readonly url: string;
  constructor(url: string) {
    super();
    this.url = url;
    FakeSocket.made.push(this);
  }
  push(data: unknown): void {
    this.dispatchEvent(new MessageEvent('message', { data: JSON.stringify(data) }));
  }
  drop(): void {
    this.dispatchEvent(new Event('close'));
  }
}

describe('the user feed', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    FakeSocket.made = [];
    vi.stubGlobal('WebSocket', FakeSocket);
    vi.stubGlobal('location', { protocol: 'https:', host: 'greenglow.example.com' });
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('passes on each list, and opens again with backoff after a drop', () => {
    const lists: unknown[] = [];
    const feed = new UserFeed((sessions) => lists.push(sessions));
    feed.start();
    expect(FakeSocket.made.map((s) => s.url)).toEqual(['wss://greenglow.example.com/ws/user']);
    FakeSocket.made[0].push({ type: 'sessions', sessions: [] });
    expect(lists).toEqual([[]]);

    // Dropped twice without a list in between: 1 second, then 2.
    FakeSocket.made[0].drop();
    vi.advanceTimersByTime(999);
    expect(FakeSocket.made).toHaveLength(1);
    vi.advanceTimersByTime(1);
    expect(FakeSocket.made).toHaveLength(2);
    FakeSocket.made[1].drop();
    vi.advanceTimersByTime(1999);
    expect(FakeSocket.made).toHaveLength(2);
    vi.advanceTimersByTime(1);
    expect(FakeSocket.made).toHaveLength(3);

    // Back: the server sends the whole list again, and the backoff starts over.
    FakeSocket.made[2].push({ type: 'sessions', sessions: [{ id: 'x' }] });
    expect(lists).toHaveLength(2);
    FakeSocket.made[2].drop();
    vi.advanceTimersByTime(1000);
    expect(FakeSocket.made).toHaveLength(4);
  });
});
