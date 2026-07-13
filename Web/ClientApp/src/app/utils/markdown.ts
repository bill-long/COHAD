import { SecurityContext } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { marked, Renderer } from 'marked';

export interface RenderMarkdownOptions {
  /** Wrap the first letter of the first paragraph in a drop-cap span for shape-outside support. */
  dropCap?: boolean;
}

/** Escape a value for safe interpolation into a double-quoted HTML attribute. Angular's sanitizer
 *  runs afterward, but escaping here prevents a stray quote/angle-bracket from breaking out of the
 *  attribute before sanitization sees it. */
function escapeHtmlAttribute(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/"/g, '&quot;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}

/** Normalize an image URL the way marked's default renderer does (encodeURI, preserving any existing
 *  percent-encoding), returning null for a malformed URL so the image is omitted. Mirrors marked's
 *  internal cleanUrl - since we replace the default image renderer we must reapply it, matching the
 *  default link renderer, which still cleans its href. */
function cleanImageUrl(href: string): string | null {
  try {
    return encodeURI(href).replace(/%25/g, '%');
  } catch {
    return null;
  }
}

/** Render a markdown string to sanitized HTML, stripping raw HTML tags. */
export function renderMarkdownToHtml(
  markdown: string,
  sanitizer: DomSanitizer,
  options?: RenderMarkdownOptions,
): SafeHtml {
  const renderer = new Renderer();
  renderer.html = () => '';
  // Derive image alt from the author's caption, never from the raw markdown alt. In blog and event
  // content (both authored through the shared milkdown editor) the alt field is never a real
  // description: drag/paste inserts derive it from the file name (often the blob-storage GUID), and
  // the milkdown image-block feature serializes the image's aspect ratio into the alt field (e.g.
  // `1.00`) while storing the real caption in the markdown title. Reading a GUID or ratio aloud is
  // worse than nothing, so use the caption (title) as alt when present and treat the image as
  // decorative (alt="") otherwise.
  renderer.image = ({ href, title }) => {
    const cleanedHref = cleanImageUrl(href ?? '');
    if (cleanedHref === null) {
      return '';
    }
    const src = escapeHtmlAttribute(cleanedHref);
    const caption = (title ?? '').trim();
    if (!caption) {
      return `<img src="${src}" alt="">`;
    }
    const escapedCaption = escapeHtmlAttribute(caption);
    return `<img src="${src}" alt="${escapedCaption}" title="${escapedCaption}">`;
  };
  let rawHtml = marked.parse(markdown, { async: false, renderer }) as string;
  if (options?.dropCap) {
    rawHtml = injectDropCapSpan(rawHtml);
  }
  const sanitized = sanitizer.sanitize(SecurityContext.HTML, rawHtml) ?? '';
  return sanitizer.bypassSecurityTrustHtml(sanitized);
}

// Letters where the right side of the glyph narrows toward the top:
// A — diagonal right stroke, wide at the foot
// L — tall vertical on the left with a foot extending right; top-right of bounding box is empty
const NARROW_TOP_RIGHT_LETTERS = new Set(['A', 'L']);

function getDropCapClassName(letter: string): string {
  const extraClass = NARROW_TOP_RIGHT_LETTERS.has(letter) ? ' drop-cap-inset' : '';
  return `drop-cap${extraClass}`;
}

/**
 * Wraps the first letter of the first paragraph in a `<span class="drop-cap">` so that
 * CSS `shape-outside` can allow text to flow around the visual ink of the letter rather
 * than the rectangular bounding box used by `::first-letter`. The paragraph also receives
 * the `drop-cap-para` class so CSS selectors can target it reliably regardless of its
 * position in the content (e.g. when a heading precedes the first paragraph).
 * Letters whose glyph leaves the top-right of the bounding box relatively empty (A, L)
 * receive an extra `drop-cap-inset` class to further improve text flow.
 *
 * Uses a DOM-based approach so the first letter is found even when it is inside inline
 * markup (e.g. `**Bold** start`). Falls back to a non-DOM HTML scan that locates the
 * first top-level paragraph and then its first letter when DOMParser is unavailable
 * (e.g. Node.js SSR environments).
 */
export function injectDropCapSpan(html: string): string {
  const domResult = injectDropCapSpanWithDom(html);
  if (domResult !== null) {
    return domResult;
  }
  return injectDropCapSpanWithoutDom(html);
}

const VOID_HTML_ELEMENTS = new Set([
  'area',
  'base',
  'br',
  'col',
  'embed',
  'hr',
  'img',
  'input',
  'link',
  'meta',
  'param',
  'source',
  'track',
  'wbr',
]);

/** Non-DOM fallback for environments without DOMParser (e.g. Node.js SSR).
 *  Uses depth-tracking to find only the first *top-level* paragraph.
 *  Scans the paragraph HTML to skip leading inline tags (e.g. `<strong>`)
 *  and find the first letter, mirroring the DOM TreeWalker behavior. */
export function injectDropCapSpanWithoutDom(html: string): string {
  const firstParagraph = findFirstTopLevelParagraph(html);
  if (!firstParagraph) {
    return html;
  }

  const paragraphInnerHtml = html.slice(firstParagraph.contentStart, firstParagraph.contentEnd);
  const openingTag = html.slice(firstParagraph.openTagStart, firstParagraph.contentStart);

  // Idempotency guard: skip if already processed. Use a structural regex to avoid
  // false-positives from literal text content that happens to contain "class=\"drop-cap".
  const DROP_CAP_SPAN_PATTERN = /<span\b[^>]*\bclass\s*=\s*["'][^"']*\bdrop-cap\b/;
  if (openingTag.includes('drop-cap-para') || DROP_CAP_SPAN_PATTERN.test(paragraphInnerHtml)) {
    return html;
  }

  const letterPos = findFirstLetterInHtml(paragraphInnerHtml);
  if (!letterPos) {
    return html;
  }

  const { index, letter } = letterPos;
  const before = paragraphInnerHtml.slice(0, index);
  const after = paragraphInnerHtml.slice(index + letter.length);
  const updatedOpeningTag = addDropCapParagraphClass(openingTag);
  const replacementInnerHtml = `${before}<span class="${getDropCapClassName(letter)}">${letter}</span>${after}`;

  return (
    html.slice(0, firstParagraph.openTagStart) +
    updatedOpeningTag +
    replacementInnerHtml +
    html.slice(firstParagraph.contentEnd)
  );
}

/** Scan an HTML string and return the index and value of the first letter character
 *  in text content (skipping tags and leading whitespace). Returns null if the first
 *  non-whitespace text character is not a letter (e.g. starts with digits or punctuation).
 *  Iterates by Unicode code point so surrogate pairs (non-BMP letters) are handled correctly. */
function findFirstLetterInHtml(html: string): { index: number; letter: string } | null {
  const tagPattern = /<[^>]+>/g;
  let pos = 0;

  while (pos < html.length) {
    tagPattern.lastIndex = pos;
    const tagMatch = tagPattern.exec(html);
    if (tagMatch && tagMatch.index === pos) {
      pos = tagPattern.lastIndex;
      continue;
    }
    const codePoint = html.codePointAt(pos)!;
    const char = String.fromCodePoint(codePoint);
    const charLength = codePoint > 0xffff ? 2 : 1;
    if (char === ' ' || char === '\t' || char === '\n' || char === '\r') {
      pos += charLength;
      continue;
    }
    // First non-whitespace, non-tag character — must be a letter or we bail
    if (/[\p{L}]/u.test(char)) {
      return { index: pos, letter: char };
    }
    return null;
  }

  return null;
}

function addDropCapParagraphClass(openingTag: string): string {
  const classAttributePattern = /\bclass\s*=\s*(["'])(.*?)\1/i;
  const classAttributeMatch = classAttributePattern.exec(openingTag);
  if (!classAttributeMatch) {
    return openingTag.replace(/^<p\b/i, '<p class="drop-cap-para"');
  }

  const quote = classAttributeMatch[1];
  const classNames = classAttributeMatch[2]
    .split(/\s+/)
    .filter((className) => className.length > 0);
  const updatedClassNames = classNames.includes('drop-cap-para')
    ? classNames.join(' ')
    : [...classNames, 'drop-cap-para'].join(' ');
  return openingTag.replace(classAttributePattern, `class=${quote}${updatedClassNames}${quote}`);
}

function findFirstTopLevelParagraph(
  html: string,
): { openTagStart: number; contentStart: number; contentEnd: number } | null {
  const tagPattern = /<\/?([a-zA-Z][\w:-]*)(?:\s[^<>]*)?\s*\/?>/g;
  let depth = 0;
  let match: RegExpExecArray | null;

  while ((match = tagPattern.exec(html)) !== null) {
    const tag = match[0];
    const tagName = match[1].toLowerCase();
    const isClosingTag = tag.startsWith('</');
    const isSelfClosingTag = /\/>$/.test(tag) || VOID_HTML_ELEMENTS.has(tagName);

    if (isClosingTag) {
      depth = Math.max(0, depth - 1);
      continue;
    }

    const currentDepth = depth;
    if (!isSelfClosingTag) {
      depth++;
    }

    if (tagName !== 'p' || currentDepth !== 0) {
      continue;
    }

    const contentStart = tagPattern.lastIndex;
    const closingTagPattern = /<\/?([a-zA-Z][\w:-]*)(?:\s[^<>]*)?\s*\/?>/g;
    closingTagPattern.lastIndex = contentStart;
    let paragraphDepth = 1;
    let innerMatch: RegExpExecArray | null;

    while ((innerMatch = closingTagPattern.exec(html)) !== null) {
      const innerTag = innerMatch[0];
      const innerTagName = innerMatch[1].toLowerCase();
      const innerIsClosingTag = innerTag.startsWith('</');
      const innerIsSelfClosingTag = /\/>$/.test(innerTag) || VOID_HTML_ELEMENTS.has(innerTagName);

      if (innerTagName !== 'p') {
        continue;
      }

      if (innerIsClosingTag) {
        paragraphDepth--;
        if (paragraphDepth === 0) {
          return { openTagStart: match.index, contentStart, contentEnd: innerMatch.index };
        }
        continue;
      }

      if (!innerIsSelfClosingTag) {
        paragraphDepth++;
      }
    }

    return null;
  }

  return null;
}

function injectDropCapSpanWithDom(html: string): string | null {
  if (typeof DOMParser === 'undefined' || typeof document === 'undefined') {
    return null;
  }

  const parser = new DOMParser();
  const parsed = parser.parseFromString(html, 'text/html');
  const firstParagraph = Array.from(parsed.body.children).find(
    (element): element is HTMLParagraphElement => element.tagName === 'P',
  );
  if (!firstParagraph) {
    return html;
  }

  // Idempotency guard: skip if the paragraph was already processed.
  if (firstParagraph.classList.contains('drop-cap-para') || firstParagraph.querySelector('.drop-cap')) {
    return html;
  }

  // Walk text nodes in document order. Skip leading whitespace-only text nodes so
  // that `<p> <strong>Bold</strong>` is handled: the whitespace text node is skipped
  // and the letter is found inside the inline element. Stop at the first non-whitespace
  // text node; if it doesn't start with a letter, don't inject.
  const walker = parsed.createTreeWalker(firstParagraph, NodeFilter.SHOW_TEXT);
  let firstTextNode: Text | null = null;
  let match: RegExpExecArray | null = null;

  for (let node = walker.nextNode() as Text | null; node; node = walker.nextNode() as Text | null) {
    if (!node.data.trim()) {
      continue;
    }
    match = /^(\s*)([\p{L}])/u.exec(node.data);
    if (!match) {
      return html;
    }
    firstTextNode = node;
    break;
  }

  if (!firstTextNode || !match) {
    return html;
  }

  const whitespace = match[1];
  const letter = match[2];
  const after = firstTextNode.data.slice(whitespace.length + letter.length);
  const parent = firstTextNode.parentNode!;

  if (whitespace) {
    parent.insertBefore(parsed.createTextNode(whitespace), firstTextNode);
  }
  const span = parsed.createElement('span');
  span.className = getDropCapClassName(letter);
  span.textContent = letter;
  parent.insertBefore(span, firstTextNode);
  if (after) {
    parent.insertBefore(parsed.createTextNode(after), firstTextNode);
  }
  parent.removeChild(firstTextNode);

  firstParagraph.classList.add('drop-cap-para');
  return parsed.body.innerHTML;
}

const entityDecoder = typeof document !== 'undefined' ? document.createElement('textarea') : null;

function decodeHtmlEntities(text: string): string {
  if (entityDecoder) {
    entityDecoder.innerHTML = text;
    return entityDecoder.value;
  }
  return text;
}

/** Strip markdown syntax to produce plain text (for summaries / card descriptions).
 *  Preserves paragraph/block-level breaks so callers can render them with white-space: pre-line.
 *  Soft line breaks within a single paragraph are collapsed to spaces. */
export function stripMarkdownToPlainText(markdown: string): string {
  const renderer = new Renderer();
  renderer.html = () => '';
  renderer.image = () => '';
  const html = marked.parse(markdown, { async: false, renderer }) as string;
  const BLOCK_BREAK = '\u0000';
  const stripped = html
    .replace(/<\/(?:p|h[1-6]|li|blockquote|div|tr)>/gi, BLOCK_BREAK)
    .replace(/<br\s*\/?>/gi, BLOCK_BREAK)
    .replace(/<[^>]*>/g, ' ')
    .replace(/\s+/g, ' ')
    .split(BLOCK_BREAK)
    .map(segment => segment.trim())
    .filter(segment => segment.length > 0)
    .join('\n')
    .trim();
  return decodeHtmlEntities(stripped);
}
