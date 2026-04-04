import { Component, ElementRef, OnInit, ViewChild } from '@angular/core';
import { HttpEventType } from '@angular/common/http';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { DocumentsService, ResidentDocument } from 'src/app/services/documents.service';
import { DocumentFolderService, DocumentFolder } from 'src/app/services/document-folder.service';
import { ConfirmDialogComponent } from '../confirm-dialog/confirm-dialog.component';
import {
  formatFileSize,
  getFileIconName,
  getFileTypeChipLabel
} from 'src/app/utils/document-display.utils';

interface FolderGroup {
  folder: DocumentFolder;
  documents: ResidentDocument[];
  collapsed: boolean;
}

@Component({
  selector: 'app-manage-documents',
  templateUrl: './manage-documents.component.html',
  styleUrls: ['./manage-documents.component.css'],
  standalone: false
})
export class ManageDocumentsComponent implements OnInit {
  @ViewChild('fileInput') fileInput?: ElementRef<HTMLInputElement>;

  folders: DocumentFolder[] = [];
  folderGroups: FolderGroup[] = [];
  unfiledDocuments: ResidentDocument[] = [];
  loading = false;
  uploadInProgress = false;
  uploadProgress = 0;
  deletingId: string | null = null;
  selectedFile: File | null = null;
  selectedFolderId = '';
  dragActive = false;
  error = '';

  newFolderName = '';
  creatingFolder = false;
  editingFolderId: string | null = null;
  editingFolderName = '';
  deletingFolderId: string | null = null;

  constructor(
    private readonly documentsService: DocumentsService,
    private readonly folderService: DocumentFolderService,
    private readonly snackBar: MatSnackBar,
    private readonly dialog: MatDialog) { }

  ngOnInit(): void {
    this.loadAll();
  }

  // --- Folder management ---

  createFolder(): void {
    const name = this.newFolderName.trim();
    if (!name) { return; }
    this.creatingFolder = true;
    this.folderService.create(name).subscribe({
      next: () => {
        this.newFolderName = '';
        this.creatingFolder = false;
        this.snackBar.open('Folder created.', 'Dismiss', { duration: 2500 });
        this.loadAll();
      },
      error: () => {
        this.creatingFolder = false;
        this.error = 'Failed to create folder.';
      }
    });
  }

  startEditFolder(folder: DocumentFolder): void {
    this.editingFolderId = folder.id;
    this.editingFolderName = folder.name;
  }

  cancelEditFolder(): void {
    this.editingFolderId = null;
    this.editingFolderName = '';
  }

  saveEditFolder(folder: DocumentFolder): void {
    const name = this.editingFolderName.trim();
    if (!name) { return; }
    this.folderService.update(folder.id, name, folder.sortOrder).subscribe({
      next: () => {
        this.editingFolderId = null;
        this.editingFolderName = '';
        this.snackBar.open('Folder renamed.', 'Dismiss', { duration: 2500 });
        this.loadAll();
      },
      error: () => {
        this.error = 'Failed to rename folder.';
      }
    });
  }

  deleteFolder(folder: DocumentFolder): void {
    const ref = this.dialog.open(ConfirmDialogComponent, {
      data: {
        title: 'Delete folder?',
        body: `This will permanently remove the folder "${folder.name}".\n\nYou can't undo this.`,
        confirmText: 'Delete',
        cancelText: 'Cancel',
        confirmColor: 'warn'
      }
    });

    ref.afterClosed().subscribe(confirmed => {
      if (confirmed !== true) { return; }
      this.deletingFolderId = folder.id;
      this.folderService.delete(folder.id).subscribe({
        next: () => {
          this.deletingFolderId = null;
          this.snackBar.open('Folder deleted.', 'Dismiss', { duration: 2500 });
          this.loadAll();
        },
        error: () => {
          this.deletingFolderId = null;
          this.error = 'Cannot delete folder. Remove its documents first.';
        }
      });
    });
  }

  moveFolderUp(folder: DocumentFolder): void {
    const idx = this.folders.findIndex(f => f.id === folder.id);
    if (idx <= 0) { return; }
    this.swapFolderOrder(idx, idx - 1);
  }

  moveFolderDown(folder: DocumentFolder): void {
    const idx = this.folders.findIndex(f => f.id === folder.id);
    if (idx < 0 || idx >= this.folders.length - 1) { return; }
    this.swapFolderOrder(idx, idx + 1);
  }

  // --- Upload ---

  downloadFile(doc: ResidentDocument): void {
    this.documentsService.download(doc.id).subscribe(blob => {
      const url = window.URL.createObjectURL(blob);
      const downloadLink = window.document.createElement('a');
      downloadLink.href = url;
      downloadLink.setAttribute('download', doc.displayName);
      window.document.body.appendChild(downloadLink);
      downloadLink.click();
      downloadLink.remove();
      window.URL.revokeObjectURL(url);
    });
  }

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.selectedFile = input.files?.item(0) ?? null;
    this.error = '';
  }

  triggerFilePicker(): void {
    if (!this.uploadInProgress) {
      this.fileInput?.nativeElement.click();
    }
  }

  onDragOver(event: DragEvent): void {
    event.preventDefault();
    this.dragActive = true;
  }

  onDragLeave(event: DragEvent): void {
    event.preventDefault();
    this.dragActive = false;
  }

  onDrop(event: DragEvent): void {
    event.preventDefault();
    this.dragActive = false;
    const file = event.dataTransfer?.files?.item(0);
    if (file) {
      this.selectedFile = file;
      this.error = '';
    }
  }

  clearSelectedFile(): void {
    this.selectedFile = null;
    this.resetFileInput();
  }

  uploadDocument(): void {
    if (!this.selectedFile) {
      this.error = 'Select a file to upload.';
      return;
    }

    this.error = '';
    this.uploadInProgress = true;
    this.uploadProgress = 0;
    const folderId = this.selectedFolderId || undefined;
    this.documentsService.upload(this.selectedFile, folderId).subscribe({
      next: event => {
        if (event.type === HttpEventType.UploadProgress && event.total) {
          this.uploadProgress = Math.round((event.loaded / event.total) * 100);
        }

        if (event.type === HttpEventType.Response) {
          this.uploadInProgress = false;
          this.uploadProgress = 100;
          this.selectedFile = null;
          this.resetFileInput();
          this.snackBar.open('Document uploaded.', 'Dismiss', { duration: 2500 });
          this.loadAll();
        }
      },
      error: () => {
        this.uploadInProgress = false;
        this.uploadProgress = 0;
        this.error = 'Upload failed.';
      }
    });
  }

  // --- Document delete ---

  deleteDocument(doc: ResidentDocument): void {
    const ref = this.dialog.open(ConfirmDialogComponent, {
      data: {
        title: 'Delete document?',
        body: `This will permanently remove "${doc.displayName}".\n\nYou can't undo this.`,
        confirmText: 'Delete',
        cancelText: 'Cancel',
        confirmColor: 'warn'
      }
    });

    ref.afterClosed().subscribe(confirmed => {
      if (confirmed !== true) {
        return;
      }

      this.error = '';
      this.deletingId = doc.id;
      this.documentsService.delete(doc.id).subscribe({
        next: () => {
          this.deletingId = null;
          this.snackBar.open('Document deleted.', 'Dismiss', { duration: 2500 });
          this.loadAll();
        },
        error: () => {
          this.deletingId = null;
          this.error = 'Delete failed.';
        }
      });
    });
  }

  // --- Helpers ---

  toggleFolder(group: FolderGroup): void {
    group.collapsed = !group.collapsed;
  }

  formatSize(sizeBytes: number): string {
    return formatFileSize(sizeBytes);
  }

  getFileTypeChip(doc: ResidentDocument): string {
    return getFileTypeChipLabel(doc);
  }

  getFileIcon(doc: ResidentDocument): string {
    return getFileIconName(doc);
  }

  get hasDocuments(): boolean {
    return this.folderGroups.some(g => g.documents.length > 0) || this.unfiledDocuments.length > 0;
  }

  private loadAll(): void {
    this.loading = true;
    this.folderService.getAll().subscribe({
      next: folders => {
        this.folders = folders;
        this.documentsService.getAll().subscribe({
          next: docs => {
            this.buildGroups(folders, docs ?? []);
            this.loading = false;
          },
          error: () => {
            this.loading = false;
            this.error = 'Failed to load documents.';
          }
        });
      },
      error: () => {
        this.loading = false;
        this.error = 'Failed to load data.';
      }
    });
  }

  private buildGroups(folders: DocumentFolder[], docs: ResidentDocument[]): void {
    const byFolder = new Map<string, ResidentDocument[]>();
    const unfiled: ResidentDocument[] = [];

    for (const doc of docs) {
      if (doc.folderId) {
        const list = byFolder.get(doc.folderId) ?? [];
        list.push(doc);
        byFolder.set(doc.folderId, list);
      } else {
        unfiled.push(doc);
      }
    }

    this.folderGroups = folders.map(f => ({
      folder: f,
      documents: byFolder.get(f.id) ?? [],
      collapsed: false
    }));

    this.unfiledDocuments = unfiled;
  }

  private swapFolderOrder(indexA: number, indexB: number): void {
    const a = this.folders[indexA];
    const b = this.folders[indexB];
    const tempSort = a.sortOrder;
    a.sortOrder = b.sortOrder;
    b.sortOrder = tempSort;

    this.folderService.reorder([
      { id: a.id, sortOrder: a.sortOrder },
      { id: b.id, sortOrder: b.sortOrder }
    ]).subscribe({
      next: () => this.loadAll(),
      error: () => { this.error = 'Failed to reorder folders.'; }
    });
  }

  private resetFileInput(): void {
    if (this.fileInput?.nativeElement) {
      this.fileInput.nativeElement.value = '';
    }
  }
}
