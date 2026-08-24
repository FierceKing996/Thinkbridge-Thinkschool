import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';

import { Quote } from './quote';

describe('Quote', () => {
  let service: Quote;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient()],
    });
    service = TestBed.inject(Quote);
  });

  it('should be created', () => {
    expect(service).toBeTruthy();
  });
});
