import { DestroyRef } from '@angular/core';
import { of } from 'rxjs';
import { UserComponent } from './user.component';
import { ApiUser, Home } from '../../models';
import { initialStateValue } from '../../state';

/**
 * The Users editor enforces the self-association rule client-side: the signed-in administrator's
 * own existing home chips are not removable, newly added ones are, other accounts are untouched,
 * and nothing is removable until the signed-in account is known. These states are what keep the
 * removal control from re-appearing on the caller's own account.
 */
describe('UserComponent own-home protection', () => {
  function home(id: string): Home {
    return { id, streetNumber: 1, streetName: 'Main' } as Home;
  }

  function user(uniqueId: string, ...ownedHomes: Home[]): ApiUser {
    return { uniqueId, roles: ['Administrator', 'Resident'], ownedHomes } as unknown as ApiUser;
  }

  function createComponent(signedIn: ApiUser | null, editing: ApiUser): UserComponent {
    const destroyRef = { onDestroy: () => () => undefined } as unknown as DestroyRef;
    const component = new UserComponent(of({ ...initialStateValue, apiUser: signedIn }), {} as never, {} as never, destroyRef);
    component.apiUser = editing;
    component.allHomes = [];
    component.ngOnInit();
    return component;
  }

  it('protects a home the signed-in account already owns', () => {
    const existing = home('h-1');
    const component = createComponent(user('me', existing), user('me', existing));
    expect(component.canRemoveHome(component.apiUserCopy.ownedHomes[0])).toBeFalse();
    expect(component.isEditingOwnAccountWithHomes()).toBeTrue();
  });

  it('leaves a home added in this edit session removable on the signed-in account', () => {
    const existing = home('h-1');
    const component = createComponent(user('me', existing), user('me', existing));
    const added = home('h-2');
    component.apiUserCopy.ownedHomes.push(added);
    expect(component.canRemoveHome(added)).toBeTrue();
  });

  it('does not protect homes on another account', () => {
    const theirs = home('h-1');
    const component = createComponent(user('me'), user('someone-else', theirs));
    expect(component.canRemoveHome(component.apiUserCopy.ownedHomes[0])).toBeTrue();
    expect(component.isEditingOwnAccountWithHomes()).toBeFalse();
  });

  it('protects every home until the signed-in account is known', () => {
    const theirs = home('h-1');
    const component = createComponent(null, user('someone-else', theirs));
    expect(component.canRemoveHome(component.apiUserCopy.ownedHomes[0])).toBeFalse();
  });

  it('ignores a removal of a protected home even if invoked directly', () => {
    const existing = home('h-1');
    const component = createComponent(user('me', existing), user('me', existing));
    component.removeHome(component.apiUserCopy.ownedHomes[0]);
    expect(component.apiUserCopy.ownedHomes.length).toBe(1);
  });
});
