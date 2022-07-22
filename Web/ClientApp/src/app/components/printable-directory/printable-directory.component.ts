import { Component, Inject, Input } from "@angular/core";
import { ActivatedRoute } from "@angular/router";
import { combineLatest, map, Observable, Subject } from "rxjs";
import { PrintableDirectory } from "src/app/models";
import { Action, AddPrintableDirectory, ApplicationState, applicationState, dispatcher } from "src/app/state";

@Component({
  selector: 'app-printable-directory',
  templateUrl: './printable-directory.component.html',
  styleUrls: ['./printable-directory.component.css']
})
export class PrintableDirectoryComponent {

  editing: boolean = false;

  pd: PrintableDirectory;

  pdCopy: PrintableDirectory;

  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Subject<Action>,
    private route: ActivatedRoute) {

    this.pd = this.getNewPrintableDirectory();
    this.pdCopy = this.getNewPrintableDirectory();
    let printableDirectories = this.appState.pipe(map(s => s.printableDirectories));
    let id = this.route.paramMap.pipe(map(p => p.get('id')));
    combineLatest([printableDirectories, id])
      .subscribe(([printableDirectories, id]) => {
        if (id == 'new') {
          this.startEdit();
        } else {
          let pdToEdit = printableDirectories.find(p => p.id === id);
          if (pdToEdit) {
            this.pd = JSON.parse(JSON.stringify(pdToEdit));
            this.pdCopy = JSON.parse(JSON.stringify(pdToEdit));
          }
        }
      });
  }

  getNewPrintableDirectory(): PrintableDirectory {
    return {
      id: '',
      created: '',
      createdBy: '',
      lastUpdated: '',
      lastUpdatedBy: '',
      frontCoverDataUrl: '',
      titlePageHTML: '',
      introductionHTML: '',
      map1DataUrl: '',
      map2DataUrl: '',
      map3DataUrl: '',
      backCoverDataUrl: '',
      addExtraPageBreak: false
    };
  }

  startEdit() {
    this.editing = true;
  }

  cancelEdit () {
    this.pdCopy = JSON.parse(JSON.stringify(this.pd));
    this.editing = false;
  }

  saveChanges() {
    this.dispatcher.next(new AddPrintableDirectory(this.pdCopy));
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
    this.pdCopy.frontCoverDataUrl = await this.handleDrop(event);
  }

  async map1Drop(event: any) {
    this.pdCopy.map1DataUrl = await this.handleDrop(event);
  }

  async map2Drop(event: any) {
    this.pdCopy.map2DataUrl = await this.handleDrop(event);
  }

  async map3Drop(event: any) {
    this.pdCopy.map3DataUrl = await this.handleDrop(event);
  }

  async backCoverDrop(event: any) {
    this.pdCopy.backCoverDataUrl = await this.handleDrop(event);
  }
}
