import { Component, OnInit } from '@angular/core';
import { CommitteeCard, CommitteeService } from 'src/app/services/committee.service';

@Component({
    selector: 'app-about',
    templateUrl: './about.component.html',
    styleUrls: ['./about.component.css'],
    standalone: false
})
export class AboutComponent implements OnInit {
  committees: CommitteeCard[] = [];
  committeesLoaded = false;

  constructor(private readonly committeeService: CommitteeService) { }

  ngOnInit(): void {
    this.committeeService.getAll().subscribe({
      next: committees => {
        this.committees = committees ?? [];
        this.committeesLoaded = true;
      },
      error: () => {
        this.committeesLoaded = true;
      }
    });
  }
}
