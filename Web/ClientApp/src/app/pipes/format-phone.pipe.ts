import { Pipe, PipeTransform } from '@angular/core';
import { formatPhoneDisplay } from '../utils/format-phone';

@Pipe({
  name: 'formatPhone',
  standalone: false,
})
export class FormatPhonePipe implements PipeTransform {
  transform(value: string | null | undefined): string {
    return formatPhoneDisplay(value);
  }
}
