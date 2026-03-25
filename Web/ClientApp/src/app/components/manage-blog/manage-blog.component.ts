import { Component, OnInit } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { BlogPostDetail, BlogService } from 'src/app/services/blog.service';
import { BlogEditorDialogComponent, BlogEditorDialogData } from 'src/app/components/blog-editor-dialog/blog-editor-dialog.component';
import { ConfirmDialogComponent } from '../confirm-dialog/confirm-dialog.component';

@Component({
  selector: 'app-manage-blog',
  templateUrl: './manage-blog.component.html',
  styleUrls: ['./manage-blog.component.css'],
  standalone: false
})
export class ManageBlogComponent implements OnInit {
  posts: BlogPostDetail[] = [];
  loading = false;
  deletingId: string | null = null;
  error = '';
  success = '';

  constructor(
    private readonly blogService: BlogService,
    private readonly dialog: MatDialog
  ) { }

  ngOnInit(): void {
    this.loadPosts();
  }

  openEditor(post: BlogPostDetail | null): void {
    this.error = '';
    this.success = '';

    const ref = this.dialog.open(BlogEditorDialogComponent, {
      data: { post } as BlogEditorDialogData,
      width: '780px',
      maxWidth: 'calc(100vw - 32px)',
      maxHeight: '90vh',
      panelClass: 'blog-editor-dialog-panel'
    });

    ref.afterClosed().subscribe(result => {
      if (result === true) {
        this.success = post == null ? 'Post created.' : 'Post updated.';
        this.loadPosts();
      }
    });
  }

  deletePost(post: BlogPostDetail): void {
    const ref = this.dialog.open(ConfirmDialogComponent, {
      data: {
        title: 'Delete post?',
        body: `This will permanently delete "${post.title}".\n\nYou can't undo this.`,
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
      this.success = '';
      this.deletingId = post.id;
      this.blogService.deleteManage(post.id).subscribe({
        next: () => {
          this.deletingId = null;
          this.success = 'Post deleted.';
          this.loadPosts();
        },
        error: () => {
          this.deletingId = null;
          this.error = 'Failed to delete post.';
        }
      });
    });
  }

  private loadPosts(): void {
    this.loading = true;
    this.blogService.getManage().subscribe({
      next: posts => {
        this.posts = posts ?? [];
        this.loading = false;
      },
      error: () => {
        this.posts = [];
        this.loading = false;
        this.error = 'Failed to load posts.';
      }
    });
  }
}
