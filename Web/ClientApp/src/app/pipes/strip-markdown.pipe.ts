import { Pipe, PipeTransform } from '@angular/core';
import { stripMarkdownToPlainText } from '../utils/markdown';

@Pipe({
  name: 'stripMarkdown',
  standalone: false,
})
export class StripMarkdownPipe implements PipeTransform {
  transform(value: string | null | undefined): string {
    if (!value) {
      return '';
    }
    return stripMarkdownToPlainText(value);
  }
}
