import { describe, expect, it } from 'vitest';
import { disjointPageIds, pageFeatureIds, selectPagingCollection } from './pagination.js';

const page = (...ids: unknown[]) => ({ type: 'FeatureCollection', features: ids.map(id => ({ type: 'Feature', id })) });

describe('pagination evidence requires actual nonempty disjoint fixture pages', () => {
  it('selects the configured paging collection independently of the browser rendering layer', () => {
    expect(selectPagingCollection({ collections: [{ id: '2000' }, { id: '0' }] }, '0')).toBe('0');
    expect(selectPagingCollection({ collections: [{ id: '1000' }] }, '1000')).toBe('1000');
    for (const body of [{ collections: [{ id: '2000' }] }, { collections: [] }, {},
      { collections: [{ id: '0' }, { id: '0' }] }]) {
      expect(() => selectPagingCollection(body, '0')).toThrow(/paging collection/);
    }
  });
  it('accepts bounded unique pages and the four-row fixture preflight', () => {
    expect(pageFeatureIds(page(11, 12, 13, 14), 4)).toEqual(['11', '12', '13', '14']);
    expect(disjointPageIds(page(11, 12), page(13, 14), 2)).toEqual(['13', '14']);
  });

  it.each([page(), page(11), page(11, 12, 13), { type: 'FeatureCollection', features: null },
    { type: 'Feature', features: [{ id: 11 }, { id: 12 }] }])('rejects empty, short, oversized or malformed pages: %j', body => {
    expect(() => pageFeatureIds(body, 2)).toThrow();
  });

  it.each([undefined, null, '', ' ', {}, [], true, NaN, Infinity])('rejects undefined or malformed IDs: %j', id => {
    expect(() => pageFeatureIds(page(11, id), 2)).toThrow();
  });

  it.each([page(11, 11), page('11', 11)])('rejects duplicate IDs within a page: %j', body => {
    expect(() => pageFeatureIds(body, 2)).toThrow(/duplicate/);
  });

  it.each([page(), page(13), page(11, 12), page(12, 13), page('11', 14)])('rejects empty, partial, repeated or overlapping second pages: %j', second => {
    expect(() => disjointPageIds(page(11, 12), second, 2)).toThrow();
  });
});
