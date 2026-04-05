import { Component, OnInit } from '@angular/core';
import { AuthService } from 'src/app/services/auth.service';

@Component({
  selector: 'app-unauthorized',
  templateUrl: './unauthorized.component.html',
  styleUrls: ['./unauthorized.component.css'],
  standalone: false,
})
export class UnauthorizedComponent implements OnInit {
  constructor(private authService: AuthService) {}

  ngOnInit() {}
}
