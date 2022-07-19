import { Component, Inject } from "@angular/core";
import { map, Observable, Subject } from "rxjs";
import { PrintableDirectory } from "src/app/models";
import { Action, ApplicationState, applicationState, dispatcher } from "src/app/state";

@Component({
  selector: 'app-manage-printable-directories',
  templateUrl: './manage-printable-directories.component.html',
  styleUrls: ['./manage-printable-directories.component.css']
})
export class ManagePrintDirectoryComponent {
  printableDirectories: Observable<PrintableDirectory[]>;

  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Subject<Action>) {
      this.printableDirectories = this.appState.pipe(map(s => s.printableDirectories));
    }

  dragOver(event: any) {
    event.preventDefault();
  }

  async handleDrop(event: any): Promise<string> {
    let p = new Promise<string>((resolve, reject) => {
      let reader = new FileReader();
      reader.onloadend = (e) => {
        resolve(reader.result as string);
      }

      reader.readAsDataURL(event.dataTransfer.files[0]);
    });

    event.preventDefault();
    return p;
  }
}
