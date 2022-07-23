import { Component, Inject } from "@angular/core";
import { map, Observable, Subject, take } from "rxjs";
import { PrintableDirectory } from "src/app/models";
import { PrintableDirectoryService } from "src/app/services/printable-directory.service";
import { Action, ApplicationState, applicationState, dispatcher, LoadPrintableDirectories } from "src/app/state";

@Component({
  selector: 'app-manage-printable-directories',
  templateUrl: './manage-printable-directories.component.html',
  styleUrls: ['./manage-printable-directories.component.css']
})
export class ManagePrintableDirectoriesComponent {
  printableDirectories: Observable<PrintableDirectory[]>;

  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Subject<Action>,
    private printableDirectoryService: PrintableDirectoryService) {
      this.printableDirectories = this.appState.pipe(map(s => s.printableDirectories));

      this.printableDirectories.pipe(take(1)).subscribe(data => {
        if (data == null || data.length < 1) {
          this.dispatcher.next(new LoadPrintableDirectories());
        }
      });
    }
}
