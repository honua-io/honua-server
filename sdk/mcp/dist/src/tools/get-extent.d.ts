import { z } from "zod";
import type { HonuaClient } from "@honua/sdk-js";
export declare const schema: z.ZodObject<{
    serviceId: z.ZodString;
    layerId: z.ZodNumber;
    where: z.ZodOptional<z.ZodString>;
    geometry: z.ZodOptional<z.ZodRecord<z.ZodString, z.ZodUnknown>>;
    spatialRel: z.ZodOptional<z.ZodEnum<["intersects", "contains", "within"]>>;
}, "strip", z.ZodTypeAny, {
    serviceId: string;
    layerId: number;
    where?: string | undefined;
    geometry?: Record<string, unknown> | undefined;
    spatialRel?: "intersects" | "contains" | "within" | undefined;
}, {
    serviceId: string;
    layerId: number;
    where?: string | undefined;
    geometry?: Record<string, unknown> | undefined;
    spatialRel?: "intersects" | "contains" | "within" | undefined;
}>;
export type Input = z.infer<typeof schema>;
export declare function execute(client: HonuaClient, input: Input): Promise<{
    content: Array<{
        type: "text";
        text: string;
    }>;
}>;
//# sourceMappingURL=get-extent.d.ts.map