import { ManageCommitteesComponent } from './manage-committees.component';
import { CommitteeAdmin, CommitteeMemberAdmin } from '../../services/committee.service';

/**
 * Covers the keyboard-accessible reordering path for committee members. CDK
 * drag-and-drop has no keyboard equivalent, so moveMember is the only way to
 * reorder members without a pointer.
 */
describe('ManageCommitteesComponent member ordering', () => {
  let component: ManageCommitteesComponent;
  let committee: CommitteeAdmin;

  function member(id: string, displayName: string, displayOrder: number): CommitteeMemberAdmin {
    return {
      id,
      residentId: `res-${id}`,
      displayName,
      title: null,
      bio: null,
      hasPhoto: false,
      email: null,
      receivesForwardedEmail: false,
      photoOffsetY: 50,
      displayOrder,
    };
  }

  function names(): string[] {
    return committee.members.map(m => m.displayName);
  }

  beforeEach(() => {
    component = new ManageCommitteesComponent({} as never, {} as never, {} as never, {} as never);
    committee = {
      members: [member('1', 'Ana', 0), member('2', 'Ben', 1), member('3', 'Cleo', 2)],
    } as CommitteeAdmin;
  });

  it('moves a member up', () => {
    component.moveMember(committee, 1, -1);
    expect(names()).toEqual(['Ben', 'Ana', 'Cleo']);
  });

  it('moves a member down', () => {
    component.moveMember(committee, 1, 1);
    expect(names()).toEqual(['Ana', 'Cleo', 'Ben']);
  });

  it('renumbers displayOrder to match the new positions', () => {
    component.moveMember(committee, 2, -1);
    expect(committee.members.map(m => m.displayOrder)).toEqual([0, 1, 2]);
    expect(names()).toEqual(['Ana', 'Cleo', 'Ben']);
  });

  it('ignores a move past the start of the list', () => {
    component.moveMember(committee, 0, -1);
    expect(names()).toEqual(['Ana', 'Ben', 'Cleo']);
  });

  it('ignores a move past the end of the list', () => {
    component.moveMember(committee, 2, 1);
    expect(names()).toEqual(['Ana', 'Ben', 'Cleo']);
  });

  it('names the member in the reorder buttons accessible label', () => {
    expect(component.memberOrderLabel(member('1', 'Ana Reyes', 0))).toBe('Ana Reyes');
  });

  it('falls back to a generic label when the member has no name yet', () => {
    expect(component.memberOrderLabel(member('1', '', 0))).toBe('this member');
  });
});
