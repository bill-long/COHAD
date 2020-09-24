import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ManageHomesComponent } from './manage-homes.component';

describe('ManageHomesComponent', () => {
  let component: ManageHomesComponent;
  let fixture: ComponentFixture<ManageHomesComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      declarations: [ ManageHomesComponent ]
    })
    .compileComponents();
  });

  beforeEach(() => {
    fixture = TestBed.createComponent(ManageHomesComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
