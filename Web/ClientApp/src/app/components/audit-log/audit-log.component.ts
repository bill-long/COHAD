import { HttpClient } from '@angular/common/http';
import { Component, OnInit, ViewChild, ViewEncapsulation } from '@angular/core';
import { MatSort } from '@angular/material/sort';
import { MatTableDataSource } from '@angular/material/table';
import { AuditLogEntry } from 'src/app/models';

@Component({
  selector: 'app-audit-log',
  templateUrl: './audit-log.component.html',
  styleUrls: ['./audit-log.component.css'],
  encapsulation: ViewEncapsulation.None
})
export class AuditLogComponent implements OnInit {

  dataSource = new MatTableDataSource<AuditLogEntry>();

  @ViewChild(MatSort, { static: false }) sort!: MatSort;

  columnsToDisplay = [
    'time',
    'subjectId',
    'subjectName',
    'action',
    'userDisplayName'
  ];

  constructor(private httpClient: HttpClient) { }

  ngOnInit(): void {
    this.httpClient.get<AuditLogEntry[]>('api/auditlog').subscribe(r => this.dataSource.data = r);
  }

  ngAfterViewInit() {
    this.dataSource.sort = this.sort;
  }

}
