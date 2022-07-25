import { Component, Inject } from "@angular/core";
import { ActivatedRoute } from "@angular/router";
import { combineLatest, map, Observable, Subject } from "rxjs";
import { PrintableDirectory } from "src/app/models";
import { Action, ApplicationState, applicationState, dispatcher } from "src/app/state";

@Component({
  selector: 'app-rendered-printable-directory',
  templateUrl: './rendered-printable-directory.component.html',
  styleUrls: ['./rendered-printable-directory.component.css']
})
export class RenderedPrintableDirectoryComponent {

  selectedDirectory: Observable<PrintableDirectory | undefined>;
  includeMap = false;

  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Subject<Action>,
    private route: ActivatedRoute) {
    let printableDirectories = this.appState.pipe(map(s => s.printableDirectories));
    let id = this.route.paramMap.pipe(map(p => p.get('id')));
    this.selectedDirectory = combineLatest([printableDirectories, id])
      .pipe(
        map(
          ([printableDirectories, id]) => printableDirectories.find(p => p.id === id)
        )
      );
  }
}
