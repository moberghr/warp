import { describe, expect, it } from 'vitest';
import { errorMessage, errorStatus } from './apiError';

describe('errorMessage', () => {
  it('surfaces a plain-string 400 body', () => {
    expect(errorMessage({ response: { data: 'GoodValues is invalid: x' } })).toBe('GoodValues is invalid: x');
  });

  it('falls back to ProblemDetails, then the error message, then a default', () => {
    expect(errorMessage({ response: { data: { detail: 'bad' } } })).toBe('bad');
    expect(errorMessage({ response: { data: { title: 'Bad Request' } } })).toBe('Bad Request');
    expect(errorMessage(new Error('Network Error'))).toBe('Network Error');
    expect(errorMessage(null)).toBe('Failed to save the objective.');
  });

  it('reads the dashboard API { error } body first', () => {
    expect(errorMessage({ response: { status: 404, data: { error: 'No recorded deploy matches.' } } })).toBe('No recorded deploy matches.');
    expect(errorMessage({ response: { data: { error: 'from must be before to', title: 'Bad Request' } } })).toBe('from must be before to');
  });

  it('skips an empty { error } and uses the caller fallback when nothing else is there', () => {
    expect(errorMessage({ response: { data: { error: '' } } }, 'Failed to delete the tile.')).toBe('Failed to delete the tile.');
    expect(errorMessage({ response: { data: { error: 42 } } }, 'x')).toBe('x');
  });
});

describe('errorStatus', () => {
  it('reads the response status, or null when there was no answer', () => {
    expect(errorStatus({ response: { status: 404 } })).toBe(404);
    expect(errorStatus({ response: { status: '500' } })).toBeNull();
    expect(errorStatus(new Error('Network Error'))).toBeNull();
    expect(errorStatus(null)).toBeNull();
  });
});
