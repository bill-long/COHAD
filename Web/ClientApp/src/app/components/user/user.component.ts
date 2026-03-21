import { COMMA, ENTER } from '@angular/cdk/keycodes';
import { Component, OnInit, Input, ElementRef, ViewChild, ViewEncapsulation, Output, EventEmitter, Inject } from '@angular/core';
import { ApiUser, Home } from 'src/app/models';
import { UntypedFormControl } from '@angular/forms';
import { MatChipInputEvent } from '@angular/material/chips';
import { MatAutocompleteSelectedEvent } from '@angular/material/autocomplete';
import { Observable } from 'rxjs';
import { startWith, map } from 'rxjs/operators';
import { UserService } from 'src/app/services/user.service';
import { applicationState, ApplicationState } from 'src/app/state';

@Component({
    selector: 'app-user',
    templateUrl: './user.component.html',
    styleUrls: ['./user.component.css'],
    encapsulation: ViewEncapsulation.None,
    standalone: false
})
export class UserComponent implements OnInit {

  @Input() apiUser!: ApiUser;

  @Input() allHomes!: Home[];

  @Output() doneEvent = new EventEmitter<void>();

  apiUserCopy!: ApiUser;

  filteredHomes!: Observable<Home[]>;

  homeControl = new UntypedFormControl();

  roleControl = new UntypedFormControl();

  allRoles = ['Resident', 'WelcomeCommittee', 'GardenClub', 'SocialCommittee', 'SunshineCommittee'];

  separatorKeyCodes: number[] = [ENTER, COMMA];

  removable = true;

  saveInProgress = false;

  readonly administratorRole = 'Administrator';

  @ViewChild('homeInput') homeInput!: ElementRef<HTMLInputElement>;

  @ViewChild('roleInput') roleInput!: ElementRef<HTMLInputElement>;

  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    private userService: UserService) { }

  ngOnInit(): void {
    this.filteredHomes = this.homeControl.valueChanges.pipe(
      startWith(null),
      map((f: any) => {
        if (f == null || f.streetName || f.length < 1) {
          return this.allHomes;
        } else {
          f = f.toLowerCase();
          return this.allHomes.filter(h => `${h.streetNumber} ${h.streetName}`.toLowerCase().includes(f));
        }
      })
    );

    this.appState.pipe(map(s => s.apiUser)).subscribe(u => {
      if (u?.roles.includes(this.administratorRole) && !this.allRoles.includes(this.administratorRole)) {
        this.allRoles.push(this.administratorRole);
      }
    })

    this.apiUserCopy = JSON.parse(JSON.stringify(this.apiUser));
    this.normalizeRoleHomeConsistency();
  }

  removeHome(home: Home) {
    const index = this.apiUserCopy.ownedHomes.indexOf(home);
    if (index >= 0) {
      this.apiUserCopy.ownedHomes.splice(index, 1);
      this.normalizeRoleHomeConsistency();
    }
  }

  addHome(event: MatChipInputEvent) {
    const value = event.value;
    if (value && value.length > 0) {
      const firstSpace = value.indexOf(' ');
      if (firstSpace > 0) {
        const streetNumberAsString = value.substring(0, firstSpace);
        const streetName = value.substring(firstSpace + 1);
        let home = this.allHomes.find(h => h.streetName === streetName && h.streetNumber.toString() === streetNumberAsString);
        if (home) {
          this.ensureHomeEditRole();
          this.apiUserCopy.ownedHomes.push(home);
          this.normalizeRoleHomeConsistency();
        }
      }
    }

    if (event.input) {
      event.input.value = '';
    }

    this.homeControl.setValue(null);
  }

  selectedHome(event: MatAutocompleteSelectedEvent) {
    this.ensureHomeEditRole();

    if (this.apiUserCopy.ownedHomes == null) {
      this.apiUserCopy.ownedHomes = [];
    }

    if (this.apiUserCopy.ownedHomes.find(h => h.id == event.option.value.id) == null) {
      this.apiUserCopy.ownedHomes.push(event.option.value);
      this.normalizeRoleHomeConsistency();
    }

    this.homeInput.nativeElement.value = '';
    this.homeControl.setValue(null);
  }

  removeRole(role: string) {
    const index = this.apiUserCopy.roles.indexOf(role);
    if (index >= 0) {
      this.apiUserCopy.roles.splice(index, 1);
      this.normalizeRoleHomeConsistency();
    }
  }

  addRole(event: MatChipInputEvent) {
    const value = event.value;
    if (value && value.length > 0) {
      if (!this.canAddNonAdminRoles() && value !== this.administratorRole) {
        if (event.input) {
          event.input.value = '';
        }
        this.roleControl.setValue(null);
        return;
      }

      if (this.apiUserCopy.roles.indexOf(value) < 0) {
        this.apiUserCopy.roles.push(value);
        this.normalizeRoleHomeConsistency();
      }
    }

    if (event.input) {
      event.input.value = '';
    }

    this.roleControl.setValue(null);
  }

  selectedRole(event: MatAutocompleteSelectedEvent) {
    if (!this.canAddNonAdminRoles() && event.option.value !== this.administratorRole) {
      this.roleInput.nativeElement.value = '';
      this.roleControl.setValue(null);
      return;
    }

    if (this.apiUserCopy.roles == null) {
      this.apiUserCopy.roles = [];
    }

    if (this.apiUserCopy.roles.indexOf(event.option.value) < 0) {
      this.apiUserCopy.roles.push(event.option.value);
      this.normalizeRoleHomeConsistency();
    }

    this.roleInput.nativeElement.value = '';
    this.roleControl.setValue(null);
  }

  cancel() {
    this.doneEvent.next();
  }

  save() {
    this.saveInProgress = true;
    this.userService.saveUser(this.apiUser, this.apiUserCopy).subscribe(r => {
      this.doneEvent.next();
    });
  }

  hasAnyHomes(): boolean {
    return (this.apiUserCopy?.ownedHomes?.length ?? 0) > 0;
  }

  hasAnyRoles(): boolean {
    return (this.apiUserCopy?.roles?.length ?? 0) > 0;
  }

  canAddNonAdminRoles(): boolean {
    return this.hasAnyHomes();
  }

  canEditHomes(): boolean {
    return true;
  }

  private normalizeRoleHomeConsistency(): void {
    this.apiUserCopy.roles ??= [];
    this.apiUserCopy.ownedHomes ??= [];

    if (this.apiUserCopy.roles.length < 1) {
      this.apiUserCopy.ownedHomes = [];
    }

    if (this.apiUserCopy.ownedHomes.length < 1) {
      this.apiUserCopy.roles = this.apiUserCopy.roles.filter(r => r === this.administratorRole);
    }
  }

  private ensureHomeEditRole(): void {
    this.apiUserCopy.roles ??= [];
    if (this.apiUserCopy.roles.length < 1) {
      this.apiUserCopy.roles.push('Resident');
    }
  }

}
