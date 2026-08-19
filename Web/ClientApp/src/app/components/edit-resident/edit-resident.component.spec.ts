import { EditResidentComponent } from './edit-resident.component';
import { PhoneNumber, Resident } from '../../models';

/**
 * A resident can have several phone fields whose visible label is identical, so
 * the accessible name is what tells them apart for anyone tabbing through the
 * form. The type alone is not sufficient - two numbers can share one.
 */
describe('EditResidentComponent phone accessible names', () => {
  let component: EditResidentComponent;

  function phone(type: string): PhoneNumber {
    return { areaCode: 555, prefix: 123, lineNumber: 4567, type } as PhoneNumber;
  }

  function withPhones(...phones: PhoneNumber[]): void {
    component.resident = { phoneNumbers: phones } as Resident;
  }

  beforeEach(() => {
    component = new EditResidentComponent({} as never);
  });

  it('uses the type alone when there is only one number', () => {
    withPhones(phone('Mobile'));
    expect(component.phoneAccessibleName(component.resident.phoneNumbers[0], 0)).toBe('Mobile phone number');
  });

  it('appends the position when several numbers share a type', () => {
    withPhones(phone('Mobile'), phone('Mobile'));
    const names = component.resident.phoneNumbers.map((p, i) => component.phoneAccessibleName(p, i));

    expect(names).toEqual(['Mobile phone number 1', 'Mobile phone number 2']);
    expect(new Set(names).size).withContext('names must be distinguishable').toBe(2);
  });

  it('falls back to a generic name when the type is not set yet', () => {
    withPhones(phone(''));
    expect(component.phoneAccessibleName(component.resident.phoneNumbers[0], 0)).toBe('Phone number');
  });

  it('contains the visible label text, as WCAG 2.5.3 requires', () => {
    withPhones(phone('Work'), phone('Home'));
    for (const [i, p] of component.resident.phoneNumbers.entries()) {
      expect(component.phoneAccessibleName(p, i).toLowerCase()).toContain('phone number');
    }
  });
});
