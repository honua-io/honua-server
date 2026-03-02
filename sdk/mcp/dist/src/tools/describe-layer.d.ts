import { z } from "zod";
import type { HonuaClient } from "@honua/sdk-js";
export declare const schema: z.ZodObject<{
    serviceId: z.ZodString;
    layerId: z.ZodNumber;
}, "strip", z.ZodTypeAny, {
    serviceId: string;
    layerId: number;
}, {
    serviceId: string;
    layerId: number;
}>;
export type Input = z.infer<typeof schema>;
export declare function execute(client: HonuaClient, input: Input): Promise<{
    content: Array<{
        type: "text";
        text: string;
    }>;
}>;
//# sourceMappingURL=describe-layer.d.ts.map