import { Component, OnInit } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { AuthService } from 'src/app/services/auth.service';

@Component({
  selector: 'app-documents',
  templateUrl: './documents.component.html',
  styleUrls: ['./documents.component.css']
})
export class DocumentsComponent implements OnInit {

  folders!: { name: string, children: { name: string}[] }[];

  constructor(private httpClient: HttpClient, private authService: AuthService) {
    httpClient.get('api/document').subscribe((r: any) => this.folders = r.children);
  }

  ngOnInit() {
  }

  downloadFile(folder: string, file: string) {
    this.httpClient.get<Blob>(`api/document/${folder}/${file}`, { responseType: 'blob' as 'json' }).subscribe(r => {
      const url = window.URL.createObjectURL(r);
      const downloadLink = document.createElement('a');
      downloadLink.href = url;
      downloadLink.setAttribute('download', file);
      document.body.appendChild(downloadLink);
      downloadLink.click();
    });
  }

}
