import { DestroyRef } from '@angular/core';
import { of } from 'rxjs';
import { EditHomeComponent } from './edit-home.component';
import { ApiUser, Home, HomeAssociatedUser, Resident } from '../../models';
import { initialStateValue } from '../../state';

/**
 * Builds the component outside a TestBed, as the resident-ordering tests always have. The state
 * observable completes synchronously, so no live subscription outlives the test; the DestroyRef
 * only has to accept the registration takeUntilDestroyed makes.
 */
function createComponent(apiUser: Partial<ApiUser> | null = null, dialog: unknown = {}): EditHomeComponent {
  const destroyRef = { onDestroy: () => () => undefined } as unknown as DestroyRef;
  return new EditHomeComponent({} as never, dialog as never, of({ ...initialStateValue, apiUser: apiUser as ApiUser | null }), destroyRef);
}

/**
 * Covers the keyboard-accessible reordering path. CDK drag-and-drop has no
 * keyboard equivalent, so moveResident is the only way to reorder a home's
 * residents without a pointer - the behaviour these tests lock is what makes
 * the screen operable by keyboard at all.
 */
describe('EditHomeComponent resident ordering', () => {
  let component: EditHomeComponent;

  function resident(id: string, givenName: string, surname: string): Resident {
    return {
      id,
      homeId: 'home-1',
      givenName,
      surname,
      emailAddresses: [],
      phoneNumbers: [],
      residentType: 0,
      yearOfBirth: 0,
      collegeName: '',
    };
  }

  function names(): string[] {
    return (component.homeCopy.residents ?? []).map(r => r.givenName);
  }

  beforeEach(() => {
    component = createComponent();
    component.homeCopy = {
      residents: [resident('1', 'Ana', 'Reyes'), resident('2', 'Ben', 'Silva'), resident('3', 'Cleo', 'Tran')],
    } as Home;
  });

  it('moves a resident up', () => {
    component.moveResident(1, 0);
    expect(names()).toEqual(['Ben', 'Ana', 'Cleo']);
  });

  it('moves a resident down', () => {
    component.moveResident(0, 1);
    expect(names()).toEqual(['Ben', 'Ana', 'Cleo']);
  });

  it('ignores a move past the start of the list', () => {
    component.moveResident(0, -1);
    expect(names()).toEqual(['Ana', 'Ben', 'Cleo']);
  });

  it('ignores a move past the end of the list', () => {
    component.moveResident(2, 3);
    expect(names()).toEqual(['Ana', 'Ben', 'Cleo']);
  });

  it('names the resident in the reorder buttons accessible label', () => {
    expect(component.residentOrderLabel(resident('1', 'Ana', 'Reyes'))).toBe('Ana Reyes');
  });

  it('falls back to a generic label when the resident has no name yet', () => {
    expect(component.residentOrderLabel(resident('1', '', ''))).toBe('this resident');
  });
});

/**
 * The signed-in account must not be able to remove its own home association - a resident who did
 * would be locked out of their own home. The template shows a "Your account" marker on that row
 * instead of the Remove button, and offers the button on no row at all until the signed-in account
 * is known (the API refuses a self-removal regardless; this keeps the dead end out of the UI).
 */
describe('EditHomeComponent associated-user self-removal', () => {
  function associatedUser(uniqueId: string): HomeAssociatedUser {
    return { uniqueId, givenName: 'Ana', surname: 'Reyes', emails: 'ana@example.com', identityProvider: 'google.com' };
  }

  it('marks the signed-in account and offers removal of the others', () => {
    const component = createComponent({ uniqueId: 'me' });
    expect(component.isCurrentUser(associatedUser('me'))).toBeTrue();
    expect(component.canRemoveAssociatedUser(associatedUser('me'))).toBeFalse();
    expect(component.isCurrentUser(associatedUser('someone-else'))).toBeFalse();
    expect(component.canRemoveAssociatedUser(associatedUser('someone-else'))).toBeTrue();
  });

  it('offers removal on no row until the signed-in account is known', () => {
    // Unknown is "could be me", not "removable": before /api/me resolves (or across a principal
    // change) the button must not appear on the caller's own row.
    const component = createComponent(null);
    expect(component.isCurrentUser(associatedUser('me'))).toBeFalse();
    expect(component.canRemoveAssociatedUser(associatedUser('me'))).toBeFalse();
    expect(component.canRemoveAssociatedUser(associatedUser('someone-else'))).toBeFalse();
  });

  it('opens the confirmation dialog for another account', () => {
    const dialog = { open: jasmine.createSpy('open').and.returnValue({ afterClosed: () => of(false) }) };
    const component = createComponent({ uniqueId: 'me' }, dialog);
    component.confirmRemoveAssociatedUser(associatedUser('someone-else'));
    expect(dialog.open).toHaveBeenCalledTimes(1);
  });
});
