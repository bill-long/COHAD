import { SecurityContext } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { marked, Renderer } from 'marked';

/** Render a markdown string to sanitized HTML, stripping raw HTML tags. */
export function renderMarkdownToHtml(markdown: string, sanitizer: DomSanitizer): SafeHtml {
  const renderer = new Renderer();
  renderer.html = () => '';
  const rawHtml = marked.parse(markdown, { async: false, renderer }) as string;
  const sanitized = sanitizer.sanitize(SecurityContext.HTML, rawHtml) ?? '';
  return sanitizer.bypassSecurityTrustHtml(sanitized);
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
 *  Preserves paragraph breaks so callers can render them with white-space: pre-line. */
export function stripMarkdownToPlainText(markdown: string): string {
  const renderer = new Renderer();
  renderer.html = () => '';
  renderer.image = () => '';
  const html = marked.parse(markdown, { async: false, renderer }) as string;
  const stripped = html
    .replace(/<\/(?:p|h[1-6]|li|blockquote|div|tr)>/gi, '\n')
    .replace(/<br\s*\/?>/gi, '\n')
    .replace(/<[^>]*>/g, ' ')
    .split('\n')
    .map(line => line.replace(/\s+/g, ' ').trim())
    .join('\n')
    .replace(/\n{3,}/g, '\n\n')
    .trim();
  return decodeHtmlEntities(stripped);
}
