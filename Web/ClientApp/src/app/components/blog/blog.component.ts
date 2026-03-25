import { Component, OnInit } from '@angular/core';
import { BlogPostCard, BlogService } from 'src/app/services/blog.service';

@Component({
  selector: 'app-blog',
  templateUrl: './blog.component.html',
  styleUrls: ['./blog.component.css'],
  standalone: false
})
export class BlogComponent implements OnInit {
  posts: BlogPostCard[] = [];
  loading = false;
  error = '';

  constructor(private readonly blogService: BlogService) { }

  ngOnInit(): void {
    this.loadPosts();
  }

  private loadPosts(): void {
    this.loading = true;
    this.blogService.getPublished().subscribe({
      next: posts => {
        this.posts = posts ?? [];
        this.loading = false;
      },
      error: () => {
        this.posts = [];
        this.loading = false;
        this.error = 'Failed to load news.';
      }
    });
  }
}
