import { formatPhoneDisplay, normalizeOptionalUsPhoneForStorage, phoneDigitsOnly } from './format-phone';

describe('formatPhoneDisplay', () => {
  it('formats 10-digit US numbers', () => {
    expect(formatPhoneDisplay('9255551234')).toBe('(925) 555-1234');
    expect(formatPhoneDisplay('925-555-1234')).toBe('(925) 555-1234');
    expect(formatPhoneDisplay('(925) 555-1234')).toBe('(925) 555-1234');
  });

  it('formats 11-digit numbers with leading 1', () => {
    expect(formatPhoneDisplay('19255551234')).toBe('+1 (925) 555-1234');
    expect(formatPhoneDisplay('1 925 555 1234')).toBe('+1 (925) 555-1234');
  });

  it('formats 7-digit local numbers', () => {
    expect(formatPhoneDisplay('5551234')).toBe('555-1234');
  });

  it('appends extension when present', () => {
    expect(formatPhoneDisplay('9255551234 ext 9')).toBe('(925) 555-1234 ext. 9');
  });

  it('returns empty for nullish or blank', () => {
    expect(formatPhoneDisplay(null)).toBe('');
    expect(formatPhoneDisplay(undefined)).toBe('');
    expect(formatPhoneDisplay('   ')).toBe('');
  });
});

describe('normalizeOptionalUsPhoneForStorage', () => {
  it('allows blank', () => {
    expect(normalizeOptionalUsPhoneForStorage('')).toEqual({ ok: true, value: '' });
    expect(normalizeOptionalUsPhoneForStorage(null)).toEqual({ ok: true, value: '' });
    expect(normalizeOptionalUsPhoneForStorage('  ')).toEqual({ ok: true, value: '' });
  });

  it('formats 10-digit numbers', () => {
    expect(normalizeOptionalUsPhoneForStorage('9255551234')).toEqual({
      ok: true,
      value: '(925) 555-1234',
    });
  });

  it('strips leading 1 for 11 digits', () => {
    expect(normalizeOptionalUsPhoneForStorage('19255551234')).toEqual({
      ok: true,
      value: '(925) 555-1234',
    });
  });

  it('rejects partial numbers', () => {
    const r = normalizeOptionalUsPhoneForStorage('925555123');
    expect(r.ok).toBe(false);
    if (!r.ok) {
      expect(r.message.length).toBeGreaterThan(0);
    }
  });
});

describe('phoneDigitsOnly', () => {
  it('strips non-digits', () => {
    expect(phoneDigitsOnly('(925) 555-1234')).toBe('9255551234');
  });
});
