import { Component, ElementRef, Inject, ViewChild } from '@angular/core';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { Observable } from 'rxjs';
import { finalize, take } from 'rxjs/operators';
import { BlogPostDetail, BlogService } from 'src/app/services/blog.service';
import { applicationState, ApplicationState } from 'src/app/state';
import { httpErrorMessage } from 'src/app/utils/http-error-message';

/** Maps committee role names to their display labels shown in the author dropdown. */
const committeeDisplayNames: Record<string, string> = {
  WelcomeCommittee: 'Welcome Committee',
  GardenClub: 'Garden Club',
  Board: 'COHAD Board',
  SocialCommittee: 'Social Committee',
  SunshineCommittee: 'Sunshine Committee',
  ArchitecturalCommittee: 'Architectural Committee',
};

export interface AuthorOption {
  value: string | null;
  label: string;
}

export interface BlogEditorDialogData {
  post: BlogPostDetail | null;
}

@Component({
  selector: 'app-blog-editor-dialog',
  templateUrl: './blog-editor-dialog.component.html',
  styleUrls: ['./blog-editor-dialog.component.css'],
  standalone: false,
})
export class BlogEditorDialogComponent {
  @ViewChild('fileInput') fileInput?: ElementRef<HTMLInputElement>;

  editingPostId: string | null = null;
  private readonly initialPostTitle: string | null;

  title = '';
  content = '';
  excerpt = '';
  publishDate: Date | null = null;
  publishTime = '';
  removeFeaturedImage = false;
  selectedFile: File | null = null;
  existingImageFileName = '';
  editorMode: 'visual' | 'markdown' = 'visual';
  saving = false;
  error = '';

  /** Author identity dropdown options (personal name + committee roles). */
  authorOptions: AuthorOption[] = [];
  /** Currently selected author: null = personal name, role string = committee. */
  authorAsCommittee: string | null = null;

  constructor(
    private readonly blogService: BlogService,
    public readonly dialogRef: MatDialogRef<BlogEditorDialogComponent, boolean>,
    @Inject(MAT_DIALOG_DATA) public readonly data: BlogEditorDialogData,
    @Inject(applicationState) private readonly appState: Observable<ApplicationState>,
  ) {
    const post = data.post;
    if (post != null) {
      this.editingPostId = post.id;
      this.initialPostTitle = post.title;
      this.title = post.title;
      this.content = post.content ?? '';
      this.excerpt = post.excerpt ?? '';
      const pub = this.utcToFields(post.publishUtc);
      this.publishDate = pub?.date ?? null;
      this.publishTime = pub?.time ?? '';
      this.existingImageFileName = post.featuredImageDisplayName ?? '';
      this.authorAsCommittee = post.authorAsCommittee ?? null;
    } else {
      this.editingPostId = null;
      this.initialPostTitle = null;
      this.title = '';
      this.content = '';
      this.excerpt = '';
      const now = new Date();
      this.publishDate = new Date(now.getFullYear(), now.getMonth(), now.getDate());
      this.publishTime = `${String(now.getHours()).padStart(2, '0')}:${String(now.getMinutes()).padStart(2, '0')}`;
      this.existingImageFileName = '';
      this.authorAsCommittee = null;
    }
    this.removeFeaturedImage = false;
    this.selectedFile = null;

    this.appState.pipe(take(1)).subscribe(state => {
      const roles = state.apiUser?.roles ?? [];
      const personalName = [state.apiUser?.givenName, state.apiUser?.surname].filter(Boolean).join(' ') || 'Me';
      const options: AuthorOption[] = [{ value: null, label: personalName }];
      for (const role of roles) {
        if (committeeDisplayNames[role]) {
          options.push({ value: role, label: committeeDisplayNames[role] });
        }
      }
      this.authorOptions = options;
    });
  }

  get dialogTitle(): string {
    return this.editingPostId == null ? 'Create post' : 'Edit post';
  }

  get editingSubtitle(): string | null {
    return this.initialPostTitle;
  }

  get showImagePreview(): boolean {
    return (
      this.editingPostId != null &&
      this.data.post != null &&
      this.data.post.hasFeaturedImage &&
      !this.removeFeaturedImage &&
      this.selectedFile == null &&
      this.data.post.featuredImageDownloadUrl != null
    );
  }

  cancel(): void {
    this.dialogRef.close(false);
  }

  save(): void {
    if (this.saving) {
      return;
    }

    if (this.title.trim().length === 0) {
      this.error = 'Title is required.';
      return;
    }

    if (!this.content || this.content.trim().length === 0) {
      this.error = 'Content is required.';
      return;
    }

    const publishUtc = this.toUtcIsoString(this.mergeDateTime());
    if (publishUtc == null) {
      this.error = 'A valid publish date and time are required.';
      return;
    }

    this.error = '';
    this.saving = true;
    this.dialogRef.disableClose = true;

    this.blogService
      .upsertManage(
        {
          id: this.editingPostId,
          title: this.title.trim(),
          content: this.content,
          excerpt: this.excerpt.trim(),
          publishUtc,
          removeFeaturedImage: this.removeFeaturedImage,
          authorAsCommittee: this.authorAsCommittee,
        },
        this.selectedFile,
      )
      .pipe(
        finalize(() => {
          this.saving = false;
          this.dialogRef.disableClose = false;
        }),
      )
      .subscribe({
        next: () => {
          this.dialogRef.close(true);
        },
        error: err => {
          this.error = httpErrorMessage(err, 'Failed to save post.');
        },
      });
  }

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.selectedFile = input.files?.item(0) ?? null;
    if (this.selectedFile != null) {
      this.removeFeaturedImage = false;
    }
  }

  clearSelectedFile(): void {
    this.selectedFile = null;
    this.resetFileInput();
  }

  private resetFileInput(): void {
    if (this.fileInput?.nativeElement) {
      this.fileInput.nativeElement.value = '';
    }
  }

  private toUtcIsoString(d: Date | null): string | null {
    if (d == null || Number.isNaN(d.getTime())) {
      return null;
    }
    return d.toISOString();
  }

  private mergeDateTime(): Date | null {
    if (this.publishDate == null) {
      return null;
    }
    const trimmed = this.publishTime.trim();
    if (!trimmed) {
      return null;
    }
    const match = /^(\d{1,2}):(\d{2})$/.exec(trimmed);
    if (match == null) {
      return null;
    }
    const hours = Number(match[1]);
    const minutes = Number(match[2]);
    if (Number.isNaN(hours) || Number.isNaN(minutes) || hours < 0 || hours > 23 || minutes < 0 || minutes > 59) {
      return null;
    }
    const d = new Date(this.publishDate);
    d.setHours(hours, minutes, 0, 0);
    return d;
  }

  private utcToFields(utcDateTime: string): { date: Date; time: string } | null {
    const date = new Date(utcDateTime);
    if (Number.isNaN(date.getTime())) {
      return null;
    }
    const dateOnly = new Date(date.getFullYear(), date.getMonth(), date.getDate());
    const time = `${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}`;
    return { date: dateOnly, time };
  }
}
