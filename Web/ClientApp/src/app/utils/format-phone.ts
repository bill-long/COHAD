/**
 * Formats a free-form phone string for display (US-centric: 7 / 10 / 11 digits).
 * Preserves unrecognized patterns as trimmed input.
 */
export function formatPhoneDisplay(input: string | null | undefined): string {
  if (input == null) {
    return '';
  }
  const trimmed = input.trim();
  if (trimmed === '') {
    return '';
  }

  const extMatch = trimmed.match(/\b(?:ext|extension|x)\s*[:.]?\s*(\d+)\s*$/i);
  const extPart = extMatch ? ` ext. ${extMatch[1]}` : '';
  const withoutExt = extMatch ? trimmed.slice(0, extMatch.index ?? 0).trim() : trimmed;

  const digits = withoutExt.replace(/\D/g, '');

  let core: string;
  if (digits.length === 10) {
    core = `(${digits.slice(0, 3)}) ${digits.slice(3, 6)}-${digits.slice(6)}`;
  } else if (digits.length === 11 && digits[0] === '1') {
    core = `+1 (${digits.slice(1, 4)}) ${digits.slice(4, 7)}-${digits.slice(7)}`;
  } else if (digits.length === 7) {
    core = `${digits.slice(0, 3)}-${digits.slice(3)}`;
  } else if (digits.length > 0) {
    return trimmed;
  } else {
    return trimmed;
  }

  return core + extPart;
}

/** Digits only; empty string if none. */
export function phoneDigitsOnly(input: string | null | undefined): string {
  return (input ?? '').replace(/\D/g, '');
}

/**
 * Optional US phone for forms: blank is ok; otherwise require 10 digits (leading 1 optional).
 * Returns formatted display string for API storage.
 */
export function normalizeOptionalUsPhoneForStorage(
  input: string | null | undefined,
): { ok: true; value: string } | { ok: false; message: string } {
  let digits = phoneDigitsOnly(input);
  if (digits.length === 0) {
    return { ok: true, value: '' };
  }
  if (digits.length === 11 && digits[0] === '1') {
    digits = digits.slice(1);
  }
  if (digits.length === 10) {
    return { ok: true, value: formatPhoneDisplay(digits) };
  }
  return {
    ok: false,
    message: 'Enter a complete phone number (10 digits), or leave blank.',
  };
}
