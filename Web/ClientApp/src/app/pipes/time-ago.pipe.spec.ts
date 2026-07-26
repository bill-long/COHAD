import { TimeAgoPipe } from './time-ago.pipe';

describe('TimeAgoPipe', () => {
  const pipe = new TimeAgoPipe();

  function ago(ms: number): string {
    return new Date(Date.now() - ms).toISOString();
  }

  it('returns empty for null, undefined, and unparseable input', () => {
    expect(pipe.transform(null)).toBe('');
    expect(pipe.transform(undefined)).toBe('');
    expect(pipe.transform('not-a-date')).toBe('');
  });

  it('formats each magnitude coarsely', () => {
    expect(pipe.transform(ago(30 * 1000))).toBe('just now');
    expect(pipe.transform(ago(5 * 60 * 1000))).toBe('5m ago');
    expect(pipe.transform(ago(3 * 3600 * 1000))).toBe('3h ago');
    expect(pipe.transform(ago(2 * 86400 * 1000))).toBe('2d ago');
    expect(pipe.transform(ago(10 * 86400 * 1000))).toBe('1w ago');
  });

  it('falls back to a plain date beyond a month', () => {
    const old = new Date(Date.now() - 40 * 86400 * 1000);
    expect(pipe.transform(old.toISOString())).toBe(old.toLocaleDateString());
  });

  it('treats a slightly-future timestamp (clock skew) as just now', () => {
    expect(pipe.transform(ago(-30 * 1000))).toBe('just now');
  });
});
