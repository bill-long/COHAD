/**
 * Locks the colour-contrast invariant for the custom design tokens.
 *
 * These are the values that WCAG 1.4.3 (Contrast Minimum) and 1.4.11 (Non-text
 * Contrast) turn on, and they are the easiest thing in the app to regress: a
 * designer nudging one hex in styles.css has no other signal that a foreground
 * has dropped below the threshold. Reading the *computed* custom properties
 * rather than parsing the stylesheet means this tests the real cascade,
 * including the dark-theme overrides.
 */

/** Relative luminance per WCAG 2.x, from an `rgb()` / `#rrggbb` string. */
function luminance(color: string): number {
  const channels = parseColor(color).map(c => {
    const s = c / 255;
    return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
  });
  return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
}

function parseColor(color: string): number[] {
  const trimmed = color.trim();
  const rgb = trimmed.match(/^rgba?\(([^)]+)\)$/);
  if (rgb) {
    return rgb[1]
      .split(/[,/\s]+/)
      .slice(0, 3)
      .map(Number);
  }
  const hex = trimmed.replace('#', '');
  if (hex.length !== 6) {
    throw new Error(`Unsupported colour format: "${color}"`);
  }
  return [0, 2, 4].map(i => parseInt(hex.substr(i, 2), 16));
}

function contrastRatio(a: string, b: string): number {
  const la = luminance(a);
  const lb = luminance(b);
  return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05);
}

function token(name: string): string {
  // Read from <body>, not <html>: the dark palette is defined on body.dark-theme,
  // and an override on <body> does not apply to its ancestor.
  const value = getComputedStyle(document.body).getPropertyValue(name);
  if (!value.trim()) {
    throw new Error(`Token ${name} is not defined - has it been renamed in styles.css?`);
  }
  return value;
}

/**
 * The opaque surfaces text is painted on.
 *
 * `--surface-chip` and `--surface-subtle` are deliberately absent: they are
 * translucent (`rgba(..., 0.03)` / `0.07`), so a ratio computed against them
 * directly is meaningless - they have to be composited over whatever is behind
 * them first. The tokens actually paired with those backgrounds today are
 * `--text-secondary` and `--primary-sage`, which clear 4.5:1 against the
 * composited result with room to spare. If a chip ever takes a lighter
 * foreground, composite first rather than adding the raw token here.
 */
const SURFACES = ['--surface', '--surface-muted', '--surface-section'];

// Foregrounds used as normal-size text. WCAG 1.4.3 requires 4.5:1.
const TEXT_TOKENS = [
  '--text-primary',
  '--text-secondary',
  '--text-tertiary',
  '--text-muted',
  '--primary-sage',
  '--secondary-terracotta',
  '--accent-gold',
  '--success',
  '--error',
  '--warning',
  '--info-purple',
];

describe('design token contrast', () => {
  describe('light theme', () => {
    runContrastSuite();
  });

  describe('dark theme', () => {
    beforeEach(() => document.body.classList.add('dark-theme'));
    afterEach(() => document.body.classList.remove('dark-theme'));
    runContrastSuite();
  });
});

function runContrastSuite(): void {
  for (const fg of TEXT_TOKENS) {
    for (const bg of SURFACES) {
      it(`${fg} on ${bg} meets 4.5:1 for normal text`, () => {
        const ratio = contrastRatio(token(fg), token(bg));
        expect(ratio)
          .withContext(`${fg} (${token(fg).trim()}) on ${bg} (${token(bg).trim()}) is ${ratio.toFixed(2)}:1`)
          .toBeGreaterThanOrEqual(4.5);
      });
    }
  }

  for (const bg of SURFACES) {
    it(`--focus-ring on ${bg} meets 3:1 for non-text contrast`, () => {
      const ratio = contrastRatio(token('--focus-ring'), token(bg));
      expect(ratio)
        .withContext(`--focus-ring on ${bg} is ${ratio.toFixed(2)}:1`)
        .toBeGreaterThanOrEqual(3);
    });
  }
}
