import { BehaviorSubject, Subject } from 'rxjs';
import { MapComponent } from './map.component';
import { initialStateValue } from '../../state';

/**
 * The printable map renders two <app-map> instances on one page, so the SVG's
 * <title>/<desc> ids must differ per instance - otherwise the document has
 * duplicate ids and the second map's aria-labelledby resolves to the first
 * map's nodes, naming both maps identically.
 */
describe('MapComponent accessible-name ids', () => {
  function create(): MapComponent {
    return new MapComponent(new BehaviorSubject(initialStateValue) as never, new Subject() as never);
  }

  it('points aria-labelledby at its own title and desc', () => {
    const map = create();
    expect(map.titleId).toContain('map-title');
    expect(map.descId).toContain('map-desc');
    expect(map.titleId).not.toBe(map.descId);
  });

  it('gives each instance distinct ids', () => {
    const first = create();
    const second = create();

    expect(first.titleId).not.toBe(second.titleId);
    expect(first.descId).not.toBe(second.descId);
  });
});
