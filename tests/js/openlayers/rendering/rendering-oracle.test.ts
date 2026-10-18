import { describe, it, expect } from 'vitest';
import { renderingVerdict } from './rendering-oracle.js';

describe('geometry rendering negative controls', () => {
  it.each(['LineString', 'Polygon'])('does not certify %s from point marker pixels', expected => {
    expect(renderingVerdict(expected, ['Point'], 1000, 12)).toBe('fail');
  });
  it('rejects no geometry, mixed geometry, blank output and invalid pixel observations', () => {
    expect(renderingVerdict('Polygon', [], 1000, 50)).toBe('fail');
    expect(renderingVerdict('Polygon', ['Polygon', 'Point'], 1000, 50)).toBe('fail');
    expect(renderingVerdict('Polygon', ['Polygon'], 0, 50)).toBe('fail');
    expect(renderingVerdict('Polygon', ['Polygon'], NaN, 50)).toBe('fail');
  });
  it.each(['Point', 'LineString', 'Polygon'])('accepts actual %s and its own pixels', expected => {
    expect(renderingVerdict(expected, [expected], 100, 50)).toBe('pass');
  });
});
