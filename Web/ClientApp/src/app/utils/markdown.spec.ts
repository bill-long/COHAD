import { renderMarkdownToHtml, stripMarkdownToPlainText } from './markdown';
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
});
