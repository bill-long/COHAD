import { Component, Inject, Input } from "@angular/core";
import { ActivatedRoute, Router } from "@angular/router";
import Quill from "quill";
import { combineLatest, map, Observable, shareReplay, Subject, take } from "rxjs";
import { PrintableDirectory } from "src/app/models";
import { PrintableDirectoryService } from "src/app/services/printable-directory.service";
import { Action, AddPrintableDirectory, ApplicationState, applicationState, dispatcher, LoadDirectory, LoadPrintableDirectories } from "src/app/state";

@Component({
  selector: 'app-printable-directory',
  templateUrl: './printable-directory.component.html',
  styleUrls: ['./printable-directory.component.css']
})
export class PrintableDirectoryComponent {

  pdCopy: PrintableDirectory;

  id: Observable<string | null>;

  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Subject<Action>,
    private printableDirectoryService: PrintableDirectoryService,
    private route: ActivatedRoute,
    private router: Router) {

    this.pdCopy = this.getNewPrintableDirectory();
    let printableDirectories = this.appState.pipe(map(s => s.printableDirectories));
    this.id = this.route.paramMap.pipe(map(p => p.get('id')), shareReplay(1));
    combineLatest([printableDirectories, this.id])
      .subscribe(([printableDirectories, id]) => {
        if (id !== 'new') {
          let pdToEdit = printableDirectories.find(p => p.id === id);
          if (pdToEdit) {
            this.pdCopy = JSON.parse(JSON.stringify(pdToEdit));
          }
        }
      });

    printableDirectories.pipe(take(1)).subscribe(data => {
      if (data == null || data.length < 1) {
        this.dispatcher.next(new LoadPrintableDirectories());
      }
    });

    const directoryData = this.appState.pipe(map(s => s.directory));

    directoryData.pipe(take(1)).subscribe(data => {
      if (data == null || data.length < 1) {
        this.dispatcher.next(new LoadDirectory());
      }
    });

    const Block = Quill.import('blots/block');
    class MyBlock extends Block { }
    MyBlock.tagName = 'DIV';
    Quill.register('blots/block', MyBlock, true);
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
      backCoverDataUrl: '',
      addExtraPageBreak: false
    };
  }

  cancelEdit() {
    this.router.navigateByUrl(`/manage/printable-directories`);
  }

  saveChanges() {
    this.id.pipe(take(1)).subscribe(id => {
      if (id === 'new') {
        this.printableDirectoryService.addPrintableDirectoryAndReloadAll(this.pdCopy).subscribe({
          next: v => {
            if (v) {
              this.router.navigateByUrl(`/manage/printable-directories`);
            }
          }
        });
      } else {
        this.printableDirectoryService.updatePrintableDirectoryAndReloadAll(this.pdCopy).subscribe({
          next: v => {
            if (v) {
              this.router.navigateByUrl(`/manage/printable-directories`);
            }
          }
        });
      }
    });
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

  async backCoverDrop(event: any) {
    this.pdCopy.backCoverDataUrl = await this.handleDrop(event);
  }
}
