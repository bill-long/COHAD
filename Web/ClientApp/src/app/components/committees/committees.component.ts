import { Component, OnInit } from '@angular/core';
import { CommitteeCard, CommitteeService } from 'src/app/services/committee.service';

@Component({
  selector: 'app-committees',
  templateUrl: './committees.component.html',
  styleUrls: ['./committees.component.css'],
  standalone: false
})
export class CommitteesComponent implements OnInit {
  committees: CommitteeCard[] = [];
  loading = false;
  error = '';

  constructor(private readonly committeeService: CommitteeService) { }

  ngOnInit(): void {
    this.loadCommittees();
  }

  private loadCommittees(): void {
    this.loading = true;
    this.committeeService.getAll().subscribe({
      next: committees => {
        this.committees = committees ?? [];
        this.loading = false;
      },
      error: () => {
        this.committees = [];
        this.loading = false;
        this.error = 'Failed to load committees.';
      }
    });
  }
}
