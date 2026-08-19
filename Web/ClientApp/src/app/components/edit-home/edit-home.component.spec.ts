import { EditHomeComponent } from './edit-home.component';
import { Home, Resident } from '../../models';

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
    component = new EditHomeComponent(
      {} as never,
      {} as never,
    );
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
