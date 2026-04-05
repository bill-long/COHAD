import { Component, OnInit } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { CommitteeCard, CommitteeMemberCard, CommitteeService } from 'src/app/services/committee.service';
import { MemberBioDialogComponent } from '../member-bio-dialog/member-bio-dialog.component';

@Component({
  selector: 'app-committees',
  templateUrl: './committees.component.html',
  styleUrls: ['./committees.component.css'],
  standalone: false,
})
export class CommitteesComponent implements OnInit {
  committees: CommitteeCard[] = [];
  loading = false;
  error = '';

  constructor(
    private readonly committeeService: CommitteeService,
    private readonly dialog: MatDialog,
  ) {}

  ngOnInit(): void {
    this.loadCommittees();
  }

  openBio(committee: CommitteeCard, member: CommitteeMemberCard): void {
    this.dialog.open(MemberBioDialogComponent, {
      data: { member, committeeName: committee.displayName },
      autoFocus: false,
    });
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
      },
    });
  }
}
