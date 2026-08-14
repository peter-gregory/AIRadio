import { TestBed } from '@angular/core/testing';

import { Radio } from './radio';

describe('Radio', () => {
  let service: Radio;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    service = TestBed.inject(Radio);
  });

  it('should be created', () => {
    expect(service).toBeTruthy();
  });
});
