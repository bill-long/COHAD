import { Component, Inject } from "@angular/core";
import { map, Observable, Subject } from "rxjs";
import { Action, ApplicationState, applicationState, dispatcher } from "src/app/state";

@Component({
  selector: 'app-printable-directory',
  templateUrl: './printable-directory.component.html',
  styleUrls: ['./printable-directory.component.css']
})
export class PrintableDirectoryComponent {

  printDirectoryImages: Observable<{
    frontCoverDataUrl: string | null,
    mapLeftDataUrl: string | null,
    mapRightDataUrl: string | null,
    backCoverDataUrl: string | null
  }>;

  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Subject<Action>) {
      this.printDirectoryImages = this.appState.pipe(map(s => s.printDirectoryImages));
    }
}
