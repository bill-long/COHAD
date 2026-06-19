import { COMMA, ENTER } from '@angular/cdk/keycodes';
import { Component, OnInit, Input, ElementRef, ViewChild, ViewEncapsulation, Output, EventEmitter, Inject } from '@angular/core';
import { ApiUser, Home } from 'src/app/models';
import { UntypedFormControl } from '@angular/forms';
import { MatChipInputEvent } from '@angular/material/chips';
import { MatAutocompleteSelectedEvent, MatAutocompleteTrigger } from '@angular/material/autocomplete';
import { MatDialog } from '@angular/material/dialog';
import { ConfirmDialogComponent } from '../confirm-dialog/confirm-dialog.component';
import { Observable } from 'rxjs';
import { startWith, map } from 'rxjs/operators';
import { UserService } from 'src/app/services/user.service';
import { applicationState, ApplicationState } from 'src/app/state';

@Component({
  selector: 'app-user',
  templateUrl: './user.component.html',
  styleUrls: ['./user.component.css'],
  encapsulation: ViewEncapsulation.None,
  standalone: false,
})
export class UserComponent implements OnInit {
  @Input() apiUser!: ApiUser;

  @Input() allHomes!: Home[];

  @Output() doneEvent = new EventEmitter<void>();

  apiUserCopy!: ApiUser;

  filteredHomes!: Observable<Home[]>;

  homeControl = new UntypedFormControl();

  roleControl = new UntypedFormControl();

  allRoles = ['Resident', 'WelcomeCommittee', 'GardenClub', 'SocialCommittee', 'SunshineCommittee', 'ArchitecturalCommittee', 'LandscapeCommittee', 'Board'];

  separatorKeyCodes: number[] = [ENTER, COMMA];

  removable = true;

  saveInProgress = false;

  readonly administratorRole = 'Administrator';

  @ViewChild('homeInput') homeInput!: ElementRef<HTMLInputElement>;

  @ViewChild('roleInput') roleInput!: ElementRef<HTMLInputElement>;

  @ViewChild('roleAutocompleteTrigger') roleAutocompleteTrigger!: MatAutocompleteTrigger;

  @ViewChild('homeAutocompleteTrigger') homeAutocompleteTrigger!: MatAutocompleteTrigger;

  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    private userService: UserService,
    private readonly dialog: MatDialog,
  ) {}

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
      }),
    );

    this.appState.pipe(map(s => s.apiUser)).subscribe(u => {
      if (u?.roles.includes(this.administratorRole) && !this.allRoles.includes(this.administratorRole)) {
        this.allRoles.push(this.administratorRole);
      }
    });

    this.apiUserCopy = JSON.parse(JSON.stringify(this.apiUser));
    this.apiUserCopy.roles ??= [];
    this.apiUserCopy.ownedHomes ??= [];
  }

  removeHome(home: Home) {
    const index = this.apiUserCopy.ownedHomes.indexOf(home);
    if (index >= 0) {
      this.apiUserCopy.ownedHomes.splice(index, 1);
      if (!this.hasAnyHomes()) {
        this.apiUserCopy.roles = (this.apiUserCopy.roles ?? []).filter(r => r === this.administratorRole);
      }
      // Chip removal moves focus to the input and opens the home autocomplete; defer so we run after that.
      this.suppressHomeAutocompleteAfterChipRemoved();
    }
  }

  addHome(event: MatChipInputEvent) {
    const value = event.value;
    if (value && value.length > 0) {
      const firstSpace = value.indexOf(' ');
      if (firstSpace > 0) {
        const streetNumberAsString = value.substring(0, firstSpace);
        const streetName = value.substring(firstSpace + 1);
        const home = this.allHomes.find(h => h.streetName === streetName && h.streetNumber.toString() === streetNumberAsString);
        if (home) {
          this.ensureHomeEditRole();
          this.apiUserCopy.ownedHomes.push(home);
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
    }

    this.homeInput.nativeElement.value = '';
    this.homeControl.setValue(null);
  }

  removeRole(role: string) {
    const index = this.apiUserCopy.roles.indexOf(role);
    if (index >= 0) {
      this.apiUserCopy.roles.splice(index, 1);
      if (!this.hasAnyRoles()) {
        this.apiUserCopy.ownedHomes = [];
        // Chip removal moves focus to the input and opens the role autocomplete; defer so we run after that.
        this.suppressRoleAutocompleteAfterLastChipRemoved();
      }
    }
  }

  private suppressRoleAutocompleteAfterLastChipRemoved(): void {
    setTimeout(() => {
      this.roleAutocompleteTrigger?.closePanel();
      this.roleInput?.nativeElement?.blur();
      this.roleControl.setValue(null);
    }, 0);
  }

  private suppressHomeAutocompleteAfterChipRemoved(): void {
    setTimeout(() => {
      this.homeAutocompleteTrigger?.closePanel();
      this.homeInput?.nativeElement?.blur();
      this.homeControl.setValue(null);
    }, 0);
  }

  addRole(event: MatChipInputEvent) {
    const value = event.value;
    if (value && value.length > 0) {
      if (this.apiUserCopy.roles.indexOf(value) < 0) {
        this.apiUserCopy.roles.push(value);
      }
    }

    if (event.input) {
      event.input.value = '';
    }

    this.roleControl.setValue(null);
  }

  selectedRole(event: MatAutocompleteSelectedEvent) {
    if (this.apiUserCopy.roles == null) {
      this.apiUserCopy.roles = [];
    }

    if (this.apiUserCopy.roles.indexOf(event.option.value) < 0) {
      this.apiUserCopy.roles.push(event.option.value);
    }

    this.roleInput.nativeElement.value = '';
    this.roleControl.setValue(null);
  }

  cancel() {
    this.doneEvent.next();
  }

  save() {
    if (this.shouldConfirmPurgeRisk()) {
      const ref = this.dialog.open(ConfirmDialogComponent, {
        data: {
          title: 'Save changes?',
          body: 'This user has no roles and no homes. They will be eligible for purge after 30 days.',
          confirmText: 'Save',
          cancelText: 'Cancel',
          confirmColor: 'primary',
        },
      });
      ref.afterClosed().subscribe(confirmed => {
        if (confirmed === true) {
          this.runSave();
        }
      });
      return;
    }

    this.runSave();
  }

  private runSave(): void {
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

  isValidForSave(): boolean {
    this.apiUserCopy.roles ??= [];
    this.apiUserCopy.ownedHomes ??= [];

    if (this.hasAnyHomes() && !this.hasAnyRoles()) {
      return false;
    }

    if (!this.hasAnyHomes() && this.apiUserCopy.roles.some(r => r !== this.administratorRole)) {
      return false;
    }

    return true;
  }

  private shouldConfirmPurgeRisk(): boolean {
    return !this.hasAnyRoles() && !this.hasAnyHomes();
  }

  private ensureHomeEditRole(): void {
    this.apiUserCopy.roles ??= [];
    if (this.apiUserCopy.roles.length < 1) {
      this.apiUserCopy.roles.push('Resident');
    }
  }
}
