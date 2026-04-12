import { renderMarkdownToHtml, stripMarkdownToPlainText, injectDropCapSpan, injectDropCapSpanWithoutDom } from './markdown';
import { DomSanitizer } from '@angular/platform-browser';
import { SecurityContext } from '@angular/core';
import { TestBed } from '@angular/core/testing';

describe('renderMarkdownToHtml', () => {
  let sanitizer: DomSanitizer;

  beforeEach(() => {
    sanitizer = TestBed.inject(DomSanitizer);
  });

  function toHtmlString(safeHtml: unknown): string {
    // SafeHtml wraps the value; extract via sanitizer
    return sanitizer.sanitize(SecurityContext.HTML, safeHtml as any) ?? '';
  }

  it('renders basic markdown to HTML', () => {
    const result = toHtmlString(renderMarkdownToHtml('**bold** text', sanitizer));
    expect(result).toContain('<strong>bold</strong>');
  });

  it('strips raw HTML tags from input', () => {
    const result = toHtmlString(renderMarkdownToHtml('Hello <script>alert("xss")</script> world', sanitizer));
    expect(result).not.toContain('<script>');
    expect(result).toContain('Hello');
  });

  it('renders headings', () => {
    const result = toHtmlString(renderMarkdownToHtml('# Title', sanitizer));
    expect(result).toContain('Title');
  });

  it('renders links', () => {
    const result = toHtmlString(renderMarkdownToHtml('[link](https://example.com)', sanitizer));
    expect(result).toContain('href');
    expect(result).toContain('link');
  });

  it('handles empty input', () => {
    const result = toHtmlString(renderMarkdownToHtml('', sanitizer));
    expect(result).toBe('');
  });

  it('handles empty input with dropCap option', () => {
    const result = toHtmlString(renderMarkdownToHtml('', sanitizer, { dropCap: true }));
    expect(result).toBe('');
  });

  it('injects drop-cap span when dropCap option is true', () => {
    const result = toHtmlString(renderMarkdownToHtml('At the start', sanitizer, { dropCap: true }));
    expect(result).toContain('class="drop-cap');
  });

  it('does not inject drop-cap span by default', () => {
    const result = toHtmlString(renderMarkdownToHtml('At the start', sanitizer));
    expect(result).not.toContain('drop-cap');
  });
});

describe('injectDropCapSpan', () => {
  it('wraps the first letter of the first paragraph', () => {
    const html = '<p>At the start of the story</p>';
    const result = injectDropCapSpan(html);
    expect(result).toContain('class="drop-cap-para"');
    expect(result).toContain('<span class="drop-cap drop-cap-inset">A</span>');
    expect(result).toContain('t the start of the story');
  });

  it('does not wrap when first paragraph starts with a non-letter', () => {
    const html = '<p>123 numbers first</p>';
    const result = injectDropCapSpan(html);
    expect(result).toBe(html);
  });

  it('only wraps the first paragraph', () => {
    const html = '<p>First paragraph</p><p>Second paragraph</p>';
    const result = injectDropCapSpan(html);
    const matches = result.match(/<span class="drop-cap/g) ?? [];
    expect(matches.length).toBe(1);
  });

  it('adds drop-cap-inset class for letters with narrow top-right (A and L)', () => {
    expect(injectDropCapSpan('<p>At the start</p>')).toContain('class="drop-cap drop-cap-inset"');
    expect(injectDropCapSpan('<p>Lovely day</p>')).toContain('class="drop-cap drop-cap-inset"');
    expect(injectDropCapSpan('<p>Mellow non-inset</p>')).not.toContain('drop-cap-inset');
    expect(injectDropCapSpan('<p>Victory lap</p>')).not.toContain('drop-cap-inset');
    expect(injectDropCapSpan('<p>Twisting road</p>')).not.toContain('drop-cap-inset');
  });

  it('preserves leading whitespace before the first letter', () => {
    const html = '<p>  Mellow spaces</p>';
    const result = injectDropCapSpan(html);
    expect(result).toContain('drop-cap-para');
    expect(result).toContain('<span class="drop-cap">M</span>');
  });

  it('does not inject into a paragraph nested inside a blockquote', () => {
    const html = '<blockquote><p>Quoted text</p></blockquote><p>Real first paragraph</p>';
    const result = injectDropCapSpan(html);
    expect(result).toContain('class="drop-cap-para"');
    // Should inject into the top-level <p>, not the one inside the blockquote
    expect(result).toContain('<span class="drop-cap">R</span>');
    expect(result).not.toContain('<span class="drop-cap">Q</span>');
  });

  it('wraps the first letter even when it is inside inline markup', () => {
    const html = '<p><strong>Amazing</strong> day ahead</p>';
    const result = injectDropCapSpan(html);
    expect(result).toContain('class="drop-cap-para"');
    expect(result).toContain('class="drop-cap drop-cap-inset"');
    expect(result).toContain('>A</span>');
  });

  it('wraps the first letter when a whitespace text node precedes an inline element', () => {
    const html = '<p> <strong>Amazing</strong> day ahead</p>';
    const result = injectDropCapSpan(html);
    expect(result).toContain('class="drop-cap-para"');
    expect(result).toContain('class="drop-cap drop-cap-inset"');
    expect(result).toContain('>A</span>');
  });

  it('is idempotent when applied to already-processed HTML', () => {
    const html = '<p>Amazing post</p>';
    const once = injectDropCapSpan(html);
    const twice = injectDropCapSpan(once);
    expect(twice).toBe(once);
    expect(twice.split('drop-cap').length - 1).toBe(3); // from drop-cap-para, drop-cap, and drop-cap-inset
  });
});

describe('injectDropCapSpanWithoutDom (SSR fallback)', () => {
  it('wraps the first letter of the first top-level paragraph', () => {
    const html = '<p>Amazing post</p>';
    const result = injectDropCapSpanWithoutDom(html);
    expect(result).toContain('class="drop-cap-para"');
    expect(result).toContain('<span class="drop-cap drop-cap-inset">A</span>');
  });

  it('does not inject into a paragraph nested inside a blockquote', () => {
    const html = '<blockquote><p>Quoted text</p></blockquote><p>Real first paragraph</p>';
    const result = injectDropCapSpanWithoutDom(html);
    expect(result).toContain('<span class="drop-cap">R</span>');
    expect(result).not.toContain('<span class="drop-cap">Q</span>');
  });

  it('applies drop-cap-inset class for A', () => {
    const html = '<p>Autumn leaves</p>';
    const result = injectDropCapSpanWithoutDom(html);
    expect(result).toContain('class="drop-cap drop-cap-inset"');
  });

  it('does not inject when first character is not a letter', () => {
    const html = '<p>123 numeric start</p>';
    const result = injectDropCapSpanWithoutDom(html);
    expect(result).toBe(html);
  });

  it('wraps the first letter when it is inside inline markup', () => {
    const html = '<p><strong>Amazing</strong> day ahead</p>';
    const result = injectDropCapSpanWithoutDom(html);
    expect(result).toContain('class="drop-cap-para"');
    expect(result).toContain('class="drop-cap drop-cap-inset"');
    expect(result).toContain('>A</span>');
    expect(result).toContain('</span>mazing</strong>');
  });

  it('is idempotent when applied to already-processed HTML', () => {
    const html = '<p>Amazing post</p>';
    const once = injectDropCapSpanWithoutDom(html);
    const twice = injectDropCapSpanWithoutDom(once);
    expect(twice).toBe(once);
    expect(twice.split('drop-cap-para').length - 1).toBe(1);
  });

  it('returns html unchanged when there is no paragraph', () => {
    const html = '<h1>Title only</h1>';
    const result = injectDropCapSpanWithoutDom(html);
    expect(result).toBe(html);
  });
});

describe('stripMarkdownToPlainText', () => {
  it('strips bold/italic syntax', () => {
    expect(stripMarkdownToPlainText('**bold** and *italic*')).toBe('bold and italic');
  });

  it('strips heading syntax', () => {
    expect(stripMarkdownToPlainText('# Hello World')).toBe('Hello World');
  });

  it('strips link syntax', () => {
    expect(stripMarkdownToPlainText('[click here](https://example.com)')).toBe('click here');
  });

  it('removes images', () => {
    expect(stripMarkdownToPlainText('![alt](image.png)')).toBe('');
  });

  it('strips list markers', () => {
    const result = stripMarkdownToPlainText('- item one\n- item two');
    expect(result).toContain('item one');
    expect(result).toContain('item two');
  });

  it('decodes HTML entities', () => {
    expect(stripMarkdownToPlainText('A & B')).toBe('A & B');
    expect(stripMarkdownToPlainText('1 < 2 > 0')).toContain('1');
  });

  it('strips raw HTML', () => {
    expect(stripMarkdownToPlainText('<div>content</div>')).toBe('');
  });

  it('handles empty input', () => {
    expect(stripMarkdownToPlainText('')).toBe('');
  });

  it('preserves paragraph breaks', () => {
    const result = stripMarkdownToPlainText('First paragraph.\n\nSecond paragraph.');
    expect(result).toBe('First paragraph.\nSecond paragraph.');
  });

  it('collapses soft line breaks within a paragraph to spaces', () => {
    const result = stripMarkdownToPlainText('First line\nSecond line');
    expect(result).toBe('First line Second line');
  });
});
