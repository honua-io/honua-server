import type { HonuaClient } from "@honua/sdk-js";
export declare const uri = "honua://services";
export declare function read(client: HonuaClient): Promise<{
    contents: {
        uri: string;
        mimeType: "application/json";
        text: string;
    }[];
}>;
//# sourceMappingURL=services.d.ts.map