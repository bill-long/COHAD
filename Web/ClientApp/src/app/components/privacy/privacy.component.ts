import { Component, OnInit } from '@angular/core';
import { AuthService } from 'src/app/services/auth.service';

@Component({
    selector: 'app-privacy',
    template: `
        <div class="inner">
            <h1>Privacy Policy</h1>
            <div>
                <ul>
                    <li>
                        When you register on this site, we collect your name, email address,
                        and street address in Canyon Oaks.
                    </li>
                    <li>
                        Once we confirm that you live in Canyon Oaks, your account is
                        able to view the neighborhood directory. Only residents of
                        Canyon Oaks may view this directory.
                    </li>
                    <li>
                        You may configure your email addresses and phone numbers to be published
                        in the directory on this web site, or not.
                        If you choose to enter an email address or phone number which is not to
                        be published in the directory, it is visible only to the administrators
                        of the web site (currently Bill Long and Judy Johannesen).
                    </li>
                    <li>
                        If you need help removing any of your information, just let us know by
                        emailing <a href="mailto:directory@cohad.org">directory@cohad.org</a>
                    </li>
                    <li>
                        We do not sell any of your information, ever.
                    </li>
                </ul>
            </div>
        </div>
  `,
    styles: [`
        .inner {
            max-width: 600px;
            margin: 0 auto;
        }

        li {
            margin-bottom: 10px;
        }
    `]
})
export class PrivacyComponent implements OnInit {

    constructor() { }

    ngOnInit() {
    }

}
