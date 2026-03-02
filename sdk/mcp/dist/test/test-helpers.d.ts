import type { HonuaClient, HonuaServicesResponse, HonuaServiceMetadata, HonuaLayerMetadata, HonuaQueryResponse } from "@honua/sdk-js";
import { vi } from "vitest";
export interface MockHonuaClient {
    listServices: ReturnType<typeof vi.fn<() => Promise<HonuaServicesResponse>>>;
    getFeatureServiceMetadata: ReturnType<typeof vi.fn<(serviceId: string) => Promise<HonuaServiceMetadata>>>;
    getLayerMetadata: ReturnType<typeof vi.fn<(serviceId: string, layerId: number) => Promise<HonuaLayerMetadata>>>;
    queryFeatures: ReturnType<typeof vi.fn<(...args: unknown[]) => Promise<HonuaQueryResponse | Record<string, unknown>>>>;
}
export declare function createMockClient(overrides?: Partial<MockHonuaClient>): MockHonuaClient;
export declare function asClient(mock: MockHonuaClient): HonuaClient;
//# sourceMappingURL=test-helpers.d.ts.map