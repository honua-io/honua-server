const MAX_LIMIT = 2000;
const DEFAULT_LIMIT = 100;
const SPATIAL_REL_MAP = {
    intersects: "esriSpatialRelIntersects",
    contains: "esriSpatialRelContains",
    within: "esriSpatialRelWithin",
};
export function mapSpatialRel(rel) {
    if (!rel)
        return undefined;
    const mapped = SPATIAL_REL_MAP[rel];
    if (!mapped)
        throw new Error(`Unknown spatialRel "${rel}". Expected: intersects, contains, within`);
    return mapped;
}
export function clampLimit(limit) {
    const n = limit ?? DEFAULT_LIMIT;
    return Math.min(Math.max(1, n), MAX_LIMIT);
}
export function jsonText(result) {
    return { content: [{ type: "text", text: JSON.stringify(result, null, 2) }] };
}
export function encodeServiceId(serviceId) {
    return encodeURIComponent(serviceId);
}
export function decodeServiceId(encoded) {
    try {
        return decodeURIComponent(encoded);
    }
    catch {
        throw new Error(`Invalid encoded serviceId: "${encoded}"`);
    }
}
export function parseLayerId(value) {
    if (!/^\d+$/.test(value)) {
        throw new Error(`Invalid layerId: "${value}"`);
    }
    return Number.parseInt(value, 10);
}
export async function mapWithConcurrency(items, concurrency, fn) {
    if (concurrency < 1 || !Number.isFinite(concurrency)) {
        throw new Error(`Invalid concurrency value: ${concurrency}`);
    }
    const results = new Array(items.length);
    let cursor = 0;
    const workers = Array.from({ length: Math.min(concurrency, items.length || 1) }, async () => {
        while (true) {
            const index = cursor;
            cursor += 1;
            if (index >= items.length) {
                return;
            }
            results[index] = await fn(items[index], index);
        }
    });
    await Promise.all(workers);
    return results;
}
