import type { EsriSpatialRel } from "@honua/sdk-js";
export declare function mapSpatialRel(rel: string | undefined): EsriSpatialRel | undefined;
export declare function clampLimit(limit: number | undefined): number;
export declare function jsonText(result: unknown): {
    content: Array<{
        type: "text";
        text: string;
    }>;
};
export declare function encodeServiceId(serviceId: string): string;
export declare function decodeServiceId(encoded: string): string;
export declare function parseLayerId(value: string): number;
export declare function mapWithConcurrency<T, R>(items: readonly T[], concurrency: number, fn: (item: T, index: number) => Promise<R>): Promise<R[]>;
//# sourceMappingURL=helpers.d.ts.map