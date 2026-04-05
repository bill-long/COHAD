import { Component, Inject } from '@angular/core';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { CommitteeMemberCard } from 'src/app/services/committee.service';

export interface MemberBioDialogData {
  member: CommitteeMemberCard;
  committeeName: string;
}

@Component({
  selector: 'app-member-bio-dialog',
  templateUrl: './member-bio-dialog.component.html',
  styleUrls: ['./member-bio-dialog.component.css'],
  standalone: false
})
export class MemberBioDialogComponent {
  constructor(
    public dialogRef: MatDialogRef<MemberBioDialogComponent>,
    @Inject(MAT_DIALOG_DATA) public data: MemberBioDialogData
  ) { }

  close(): void {
    this.dialogRef.close();
  }
}
