import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'app-manage-print',
  templateUrl: './manage-print.component.html',
  styleUrls: ['./manage-print.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: false,
})
export class ManagePrintComponent {
  constructor() {}
}
