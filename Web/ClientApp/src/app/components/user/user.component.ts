import { COMMA, ENTER } from '@angular/cdk/keycodes';
import {
  Component,
  DestroyRef,
  OnInit,
  OnChanges,
  SimpleChanges,
  Input,
  ElementRef,
  ViewChild,
  ViewEncapsulation,
  Output,
  EventEmitter,
  Inject,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ApiUser, Home, Resident } from 'src/app/models';
import { UntypedFormControl } from '@angular/forms';
import { MatChipInputEvent } from '@angular/material/chips';
import { MatAutocompleteSelectedEvent, MatAutocompleteTrigger } from '@angular/material/autocomplete';
import { MatDialog } from '@angular/material/dialog';
import { ConfirmDialogComponent } from '../confirm-dialog/confirm-dialog.component';
import { Observable } from 'rxjs';
import { startWith, map } from 'rxjs/operators';
import { EMPTY_GUID, UserService } from 'src/app/services/user.service';
import { applicationState, ApplicationState } from 'src/app/state';

@Component({
  selector: 'app-user',
  templateUrl: './user.component.html',
  styleUrls: ['./user.component.css'],
  encapsulation: ViewEncapsulation.None,
  standalone: false,
})
export class UserComponent implements OnInit, OnChanges {
  @Input() apiUser!: ApiUser;

  @Input() allHomes!: Home[];

  @Output() doneEvent = new EventEmitter<void>();

  apiUserCopy!: ApiUser;

  /**
   * Residents of the user's currently selected homes, offered as the optional "this account is this
   * person" link. Children are excluded (they hold no email addresses and cannot own accounts).
   * Sourced from allHomes because ownedHomes entries arrive without residents populated.
   */
  linkableResidents: { resident: Resident; homeLabel: string }[] = [];

  filteredHomes!: Observable<Home[]>;

  homeControl = new UntypedFormControl();

  roleControl = new UntypedFormControl();

  allRoles = ['Resident', 'WelcomeCommittee', 'GardenClub', 'SocialCommittee', 'SunshineCommittee', 'ArchitecturalCommittee', 'LandscapeCommittee', 'Board'];

  separatorKeyCodes: number[] = [ENTER, COMMA];

  removable = true;

  private currentUserUniqueId: string | null = null;

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
    private readonly destroyRef: DestroyRef,
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

    // applicationState is a BehaviorSubject, so this receives the current user synchronously
    // before the first render; the editor is opened and closed repeatedly in one session, so the
    // subscription must not outlive the component.
    this.appState
      .pipe(
        map(s => s.apiUser),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(u => {
        this.currentUserUniqueId = u?.uniqueId ?? null;
        if (u?.roles.includes(this.administratorRole) && !this.allRoles.includes(this.administratorRole)) {
          this.allRoles.push(this.administratorRole);
        }
      });

    this.apiUserCopy = JSON.parse(JSON.stringify(this.apiUser));
    this.apiUserCopy.roles ??= [];
    this.apiUserCopy.ownedHomes ??= [];
    this.syncResidentLinkOptions();
  }

  ngOnChanges(changes: SimpleChanges): void {
    // allHomes arrives asynchronously (allHomes$ | async), so the options must be recomputed when
    // it lands or the dropdown would stay hidden for the whole edit session.
    if (changes['allHomes'] && this.apiUserCopy) {
      this.syncResidentLinkOptions();
    }
  }

  /**
   * Recomputes the linkable-resident options from the selected homes. Deliberately never clears
   * apiUserCopy.residentId: an absent option can mean allHomes simply has not loaded (or failed to
   * load), and the server is the one place that decides a retained link is no longer valid.
   */
  private syncResidentLinkOptions(): void {
    const childResidentType = 2;
    const options: { resident: Resident; homeLabel: string }[] = [];
    for (const owned of this.apiUserCopy.ownedHomes ?? []) {
      const home = (this.allHomes ?? []).find(h => h.id === owned.id);
      if (!home) {
        continue;
      }
      const homeLabel = `${home.streetNumber} ${home.streetName}`;
      // Legacy resident records can carry an all-zeros id, which collides with the wire
      // protocol's clear sentinel - never offer them as link targets.
      for (const resident of (home.residents ?? []).filter(r => r.residentType !== childResidentType && r.id !== EMPTY_GUID)) {
        options.push({ resident, homeLabel });
      }
    }
    this.linkableResidents = options;
  }

  // Deliberately no client-side link-validity logic: the client's view (cached allHomes) cannot
  // distinguish "invalid" from "not loaded yet", and every attempt to decide here has produced a
  // silent-clear bug. The selection is sent as-is; the server is the sole authority - it retains
  // an untouched link, clears an invalidated retained link with an audit note, and rejects an
  // explicitly invalid one with a message the save flow surfaces in the snackbar.

  /**
   * An administrator editing their own account may add homes and change roles, but may not drop a
   * home they already own - the server refuses it (HomeAssociationRules, the same rule the home
   * editor enforces), so the chip is not removable rather than failing on save. A home added in
   * this edit session stays removable: it is not owned yet. Until the signed-in account is known,
   * unknown is treated as "could be me" and no chip is removable, matching the home editor.
   */
  canRemoveHome(home: Home): boolean {
    if (this.currentUserUniqueId == null) {
      return false;
    }
    if (this.apiUserCopy?.uniqueId !== this.currentUserUniqueId) {
      return true;
    }
    return !(this.apiUser?.ownedHomes ?? []).some(h => h.id === home.id);
  }

  /**
   * True when the editor is open on the signed-in account and it already owns a home. Like
   * canRemoveHome, an unknown signed-in account counts as "could be me".
   */
  isEditingOwnAccountWithHomes(): boolean {
    const isOwnOrUnknown = this.currentUserUniqueId == null || this.apiUserCopy?.uniqueId === this.currentUserUniqueId;
    return isOwnOrUnknown && (this.apiUser?.ownedHomes?.length ?? 0) > 0;
  }

  removeHome(home: Home) {
    if (!this.canRemoveHome(home)) {
      return;
    }
    const index = this.apiUserCopy.ownedHomes.indexOf(home);
    if (index >= 0) {
      this.apiUserCopy.ownedHomes.splice(index, 1);
      this.syncResidentLinkOptions();
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
          this.syncResidentLinkOptions();
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
      this.syncResidentLinkOptions();
    }

    this.homeInput.nativeElement.value = '';
    this.homeControl.setValue(null);
  }

  /**
   * Removing the last role also clears the home list (an account cannot own homes with no roles),
   * so on the signed-in account it would be an indirect self-removal the server refuses. The last
   * role chip is therefore not removable while the account owns a home; every other role change on
   * one's own account remains allowed.
   */
  canRemoveRole(role: string): boolean {
    const isLastRole = (this.apiUserCopy?.roles ?? []).length === 1 && this.apiUserCopy.roles[0] === role;
    return !(isLastRole && this.isEditingOwnAccountWithHomes());
  }

  removeRole(role: string) {
    if (!this.canRemoveRole(role)) {
      return;
    }
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
    this.userService.saveUser(this.apiUser, this.apiUserCopy).subscribe(ok => {
      // saveUser maps request failures to false (after reporting the reason itself); closing the
      // editor anyway would silently discard the admin's changes, so stay open.
      if (ok) {
        this.doneEvent.next();
        return;
      }
      this.saveInProgress = false;
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
