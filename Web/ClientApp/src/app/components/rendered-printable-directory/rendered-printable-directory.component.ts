import { Component, Inject } from "@angular/core";
import { map, Observable, Subject } from "rxjs";
import { PrintableDirectory } from "src/app/models";
import { Action, ApplicationState, applicationState, dispatcher } from "src/app/state";

@Component({
  selector: 'app-rendered-printable-directory',
  templateUrl: './rendered-printable-directory.component.html',
  styleUrls: ['./rendered-printable-directory.component.css']
})
export class PrintableDirectoryComponent {

  printableDirectories: Observable<PrintableDirectory[]>;

  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Subject<Action>) {
      this.printableDirectories = this.appState.pipe(map(s => s.printableDirectories));
    }
}
