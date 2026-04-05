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

  expandedBios = new Set<string>();

  constructor(private readonly committeeService: CommitteeService) { }

  ngOnInit(): void {
    this.loadCommittees();
  }

  bioKey(committee: CommitteeCard, member: { displayName: string }): string {
    return `${committee.displayName}:${member.displayName}`;
  }

  toggleBio(committee: CommitteeCard, member: { displayName: string }): void {
    const key = this.bioKey(committee, member);
    if (this.expandedBios.has(key)) {
      this.expandedBios.delete(key);
    } else {
      this.expandedBios.add(key);
    }
  }

  isBioClamped(el: HTMLElement, committee: CommitteeCard, member: { displayName: string }): boolean {
    if (this.expandedBios.has(this.bioKey(committee, member))) {
      return true; // show "Show less" when expanded
    }
    return el.scrollHeight > el.clientHeight + 1;
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
