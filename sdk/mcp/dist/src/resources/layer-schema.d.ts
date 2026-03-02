import type { HonuaClient } from "@honua/sdk-js";
export declare const uriTemplate = "honua://services/{encodedServiceId}/layers/{layerId}";
export declare function read(client: HonuaClient, encodedServiceId: string, layerId: string): Promise<{
    contents: {
        uri: string;
        mimeType: "application/json";
        text: string;
    }[];
}>;
//# sourceMappingURL=layer-schema.d.ts.map