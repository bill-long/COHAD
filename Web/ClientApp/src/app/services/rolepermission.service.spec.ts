import { rolePermissions } from './rolepermission.service';

/**
 * These lists gate routes and navigation on the client, but the server enforces its own copies. A
 * disagreement is invisible until a user hits it: either the page loads and every API call returns
 * 403, or the guard bounces them from a page the backend would have allowed. Pinning them here
 * makes a one-sided edit fail, the same way the backend's AnyRoleAuthorizationHandlerTests does.
 */
describe('rolePermissions', () => {
  it('manageEmailRoles matches AuthorizationRoleSets.EmailSender on the server', () => {
    expect(rolePermissions.manageEmailRoles).toEqual([
      'Administrator',
      'WelcomeCommittee',
      'GardenClub',
      'Board',
      'SocialCommittee',
      'SunshineCommittee',
      'ArchitecturalCommittee',
      'LandscapeCommittee',
    ]);
  });

  it('manageCommitteesRoles matches AuthorizationRoleSets.CommitteeEditor on the server', () => {
    expect(rolePermissions.manageCommitteesRoles).toEqual([
      'Administrator',
      'WelcomeCommittee',
      'GardenClub',
      'Board',
      'SocialCommittee',
      'SunshineCommittee',
      'ArchitecturalCommittee',
      'LandscapeCommittee',
    ]);
  });

  it('grants sending per committee mailbox, never by being an Administrator', () => {
    // The backend's from-* endpoints are gated on the committee's own role, so an Administrator
    // without one cannot send. The compose card keys off these lists and must agree with that.
    expect(rolePermissions.sendEmailAsBoard).toEqual(['Board']);
    expect(rolePermissions.sendEmailAsWelcomeCommittee).toEqual(['WelcomeCommittee']);
    expect(rolePermissions.sendEmailAsGardenClub).toEqual(['GardenClub']);
    expect(rolePermissions.sendEmailAsSocialCommittee).toEqual(['SocialCommittee']);
    expect(rolePermissions.sendEmailAsSunshineCommittee).toEqual(['SunshineCommittee']);
  });
});
