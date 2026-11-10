import { describe, expect, it } from 'vitest';
import { USER_DISCONNECTED, describeEnd } from '../src/ended.ts';

describe('why a session ended', () => {
  it('says nothing when the user disconnected here', () => {
    expect(describeEnd(1000, USER_DISCONNECTED, 'Test')).toBeUndefined();
  });

  it('names the usual reasons', () => {
    const title = (code: number, reason: string) => describeEnd(code, reason, 'Test')?.title;
    expect(title(1000, 'host closed')).toBe('Session ended');
    expect(title(1000, 'host error ConnectionReset')).toBe('Connection to the IBM i lost');
    expect(title(1000, 'host error connect timeout')).toBe('Connection to the IBM i lost');
    expect(title(1000, 'server shutting down')).toBe('GreenGlow restarted');
    expect(title(1000, 'server error')).toBe('GreenGlow error');
    expect(title(1000, 'ended by the user')).toBe('Ended elsewhere');
    expect(title(1008, 'browser too slow')).toBe('Connection too slow');
    expect(title(1000, 'This session was opened in another browser or tab.')).toBe(
      'Opened elsewhere',
    );
    expect(title(1000, 'The owner stopped sharing this session with you.')).toBe('Sharing stopped');
    expect(
      title(1000, 'The owner left this session. You can join it again once they are back in it.'),
    ).toBe('Owner left');
    expect(
      title(1000, "The owner isn't in this session now. You can join it once they are back in it."),
    ).toBe('Owner left');
    expect(title(1000, 'This session already has 2 browsers attached, the most allowed.')).toBe(
      'Session full',
    );
    expect(title(1006, '')).toBe('Connection to GreenGlow lost');
    expect(title(1000, 'something new')).toBe('Session ended');
  });

  it('keeps the detail of a host error and of a lost connection', () => {
    expect(describeEnd(1000, 'host error ConnectionRefused', 'Test')?.message).toContain(
      'Test was lost or refused (ConnectionRefused)',
    );
    expect(describeEnd(1006, '', 'Test')?.message).toContain('(1006)');
    expect(describeEnd(1006, '', 'Test')?.error).toBe(true);
    expect(describeEnd(1000, 'host closed', 'Test')?.error).toBe(false);
  });
});
