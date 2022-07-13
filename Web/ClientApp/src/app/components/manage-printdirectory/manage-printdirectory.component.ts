import { Component, Inject } from "@angular/core";
import { map, Observable, Subject } from "rxjs";
import { Action, ApplicationState, applicationState, dispatcher, SetPrintDirectoryBackCover, SetPrintDirectoryFrontCover, SetPrintDirectoryLeftMap, SetPrintDirectoryRightMap } from "src/app/state";

@Component({
  selector: 'app-manage-printdirectory',
  templateUrl: './manage-printdirectory.component.html',
  styleUrls: ['./manage-printdirectory.component.css']
})
export class ManagePrintDirectoryComponent {
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

  async frontCoverDrop(event: any) {
    this.dispatcher.next(new SetPrintDirectoryFrontCover(await this.handleDrop(event)));
  }

  async mapLeftDrop(event: any) {
    this.dispatcher.next(new SetPrintDirectoryLeftMap(await this.handleDrop(event)));
  }

  async mapRightDrop(event: any) {
    this.dispatcher.next(new SetPrintDirectoryRightMap(await this.handleDrop(event)));
  }

  async backCoverDrop(event: any) {
    this.dispatcher.next(new SetPrintDirectoryBackCover(await this.handleDrop(event)));
  }

  showPrintDirectory() {
  }
}
