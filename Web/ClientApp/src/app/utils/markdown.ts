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

/** Strip markdown syntax to produce plain text (for summaries / card descriptions). */
export function stripMarkdownToPlainText(markdown: string): string {
  const renderer = new Renderer();
  renderer.html = () => '';
  renderer.image = () => '';
  const html = marked.parse(markdown, { async: false, renderer }) as string;
  const stripped = html
    .replace(/<[^>]*>/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
  // Decode HTML entities (e.g. &amp; → &)
  const el = document.createElement('textarea');
  el.innerHTML = stripped;
  return el.value;
}
