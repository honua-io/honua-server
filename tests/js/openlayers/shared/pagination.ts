/** Assertions for pagination evidence, independent of response status or counters. */
export function pageFeatureIds(body: unknown, expectedCount: number): string[] {
  if (!Number.isSafeInteger(expectedCount) || expectedCount < 1) throw new Error('Pagination requires a positive page size');
  if (!body || typeof body !== 'object' || !('type' in body) || body.type !== 'FeatureCollection'
    || !('features' in body) || !Array.isArray(body.features) || body.features.length !== expectedCount) {
    throw new Error(`Pagination requires exactly ${expectedCount} actual fixture features`);
  }
  const ids = body.features.map((feature: unknown) => {
    if (!feature || typeof feature !== 'object' || !('type' in feature) || feature.type !== 'Feature'
      || !('id' in feature) || !((typeof feature.id === 'string' && feature.id.trim().length > 0)
        || (typeof feature.id === 'number' && Number.isSafeInteger(feature.id)))) {
      throw new Error('Pagination requires a defined feature ID on every returned feature');
    }
    return String(feature.id);
  });
  if (new Set(ids).size !== ids.length) throw new Error('Pagination returned duplicate feature IDs');
  return ids;
}

export function disjointPageIds(first: unknown, second: unknown, pageSize: number): string[] {
  const firstIds = new Set(pageFeatureIds(first, pageSize));
  const secondIds = pageFeatureIds(second, pageSize);
  if (secondIds.some(id => firstIds.has(id))) throw new Error('Offset pagination repeated a feature from the first page');
  return secondIds;
}
