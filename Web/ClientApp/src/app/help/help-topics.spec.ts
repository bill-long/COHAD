import { routes } from '../app-routing.module';
import { manageNavGroups } from '../components/manage/manage-nav';
import { rolePermissions } from '../services/rolepermission.service';
import { helpTopics, topicForUrl, userCanSeeTopic } from './help-topics';

describe('helpTopics registry', () => {
  it('derives one topic per Manage rail tool, mirroring its label, group, and roles', () => {
    for (const group of manageNavGroups) {
      for (const item of group.items) {
        const topic = helpTopics.find(t => t.id === item.route);
        expect(topic).withContext(`rail tool '${item.route}' should have a help topic`).toBeDefined();
        expect(topic!.title).toBe(item.label);
        expect(topic!.section).toBe(group.label);
        expect(topic!.roles).toBe(item.roles);
        expect(topic!.requireResident).toBe(item.requireResident);
        expect(topic!.routePrefix).toBe(`/manage/${item.route}`);
      }
    }
  });

  it('every topic routePrefix matches a route under /manage in the routing table', () => {
    // The registry derives from the rail, so this locks rail <-> routing agreement: a renamed or
    // removed child route must rename the rail entry (and its doc) with it.
    const manageChildren = routes.find(r => r.path === 'manage')?.children ?? [];
    expect(manageChildren.length).toBeGreaterThan(0);
    for (const topic of helpTopics.filter(t => t.routePrefix !== '/manage')) {
      const child = topic.routePrefix.replace('/manage/', '');
      const matches = manageChildren.some(r => r.path === child || (r.path ?? '').startsWith(`${child}/`));
      expect(matches).withContext(`topic '${topic.id}' prefix '${topic.routePrefix}' should match a manage child route`).toBeTrue();
    }
  });

  it('every topic has its markdown document in assets/help', async () => {
    // Karma serves src/assets, so this locks registry entry <-> doc file agreement end to end.
    const missing = (
      await Promise.all(
        helpTopics.map(async topic => {
          const response = await fetch(`assets/help/${topic.id}.md`);
          return response.ok ? null : topic.id;
        }),
      )
    ).filter((id): id is string => id !== null);
    expect(missing).withContext('assets/help/{id}.md missing for these topics').toEqual([]);
  });

  it('every #topic: cross-reference in the shipped docs resolves to a registry id', async () => {
    // The drawer resolves #topic: links against the registry; a link to an unknown id would be a
    // silent dead click, so lock doc references here.
    const ids = new Set(helpTopics.map(t => t.id));
    const badRefs = (
      await Promise.all(
        helpTopics.map(async topic => {
          const response = await fetch(`assets/help/${topic.id}.md`);
          const text = response.ok ? await response.text() : '';
          return [...text.matchAll(/#topic:([a-z-]+)/g)]
            .map(match => match[1])
            .filter(refId => !ids.has(refId))
            .map(refId => `${topic.id}.md -> ${refId}`);
        }),
      )
    ).flat();
    expect(badRefs).withContext('docs reference unknown topic ids').toEqual([]);
  });

  it('topic ids are unique', () => {
    const ids = helpTopics.map(t => t.id);
    expect(new Set(ids).size).toBe(ids.length);
  });

  it("email-jobs inherits the Email rail entry's section and audience and sits next to it", () => {
    // The job-detail page has no rail entry; it must follow Email wherever the rail moves it.
    const emailIndex = helpTopics.findIndex(t => t.id === 'send-email');
    const emailJobs = helpTopics[emailIndex + 1];
    expect(emailJobs.id).toBe('email-jobs');
    expect(emailJobs.section).toBe(helpTopics[emailIndex].section);
    expect(emailJobs.roles).toBe(helpTopics[emailIndex].roles);
    expect(emailJobs.requireResident).toBe(helpTopics[emailIndex].requireResident);
  });
});

describe('topicForUrl', () => {
  it('picks the longest matching prefix', () => {
    expect(topicForUrl(helpTopics, '/manage/users')?.id).toBe('users');
    expect(topicForUrl(helpTopics, '/manage')?.id).toBe('new-administrator');
  });

  it('matches child paths and ignores query string and fragment', () => {
    expect(topicForUrl(helpTopics, '/manage/email-jobs/abc-123')?.id).toBe('email-jobs');
    expect(topicForUrl(helpTopics, '/manage/approvals?message=xyz')?.id).toBe('approvals');
    expect(topicForUrl(helpTopics, '/manage/send-email#email-jobs')?.id).toBe('send-email');
  });

  it('requires a segment boundary and returns null off the manage area', () => {
    expect(topicForUrl(helpTopics, '/managefoo')).toBeNull();
    expect(topicForUrl(helpTopics, '/residents/directory')).toBeNull();
    expect(topicForUrl(helpTopics, '/')).toBeNull();
  });
});

describe('userCanSeeTopic', () => {
  const usersTopic = helpTopics.find(t => t.id === 'users')!;
  const eventsTopic = helpTopics.find(t => t.id === 'events')!;
  const printTopic = helpTopics.find(t => t.id === 'print')!;

  it('requires one of the topic roles', () => {
    expect(userCanSeeTopic(usersTopic, ['Administrator', 'Resident'])).toBeTrue();
    expect(userCanSeeTopic(usersTopic, ['GardenClub', 'Resident'])).toBeFalse();
    expect(userCanSeeTopic(usersTopic, [])).toBeFalse();
  });

  it('honors requireResident like RoleGuard and the rail', () => {
    expect(eventsTopic.requireResident).toBeTrue();
    expect(userCanSeeTopic(eventsTopic, ['SocialCommittee', 'Resident'])).toBeTrue();
    // A committee member without the Resident role is bounced from /manage/events by RoleGuard,
    // so the help index must not advertise the screen to them either.
    expect(userCanSeeTopic(eventsTopic, ['SocialCommittee'])).toBeFalse();
  });

  it("mirrors the rail's narrower Print Directory gate, not the route's broader allowedRoles", () => {
    // The /manage/print route allows all manageRoles, but the rail entry (and the print-directory
    // tool itself) is Administrator-only; help visibility follows the rail.
    expect(printTopic.roles).toBe(rolePermissions.printDirectoryRoles);
    expect(userCanSeeTopic(printTopic, ['GardenClub', 'Resident'])).toBeFalse();
    expect(userCanSeeTopic(printTopic, ['Administrator', 'Resident'])).toBeTrue();
  });
});
