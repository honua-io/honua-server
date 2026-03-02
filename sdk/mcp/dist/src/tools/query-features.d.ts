import { z } from "zod";
import type { HonuaClient } from "@honua/sdk-js";
export declare const schema: z.ZodObject<{
    serviceId: z.ZodString;
    layerId: z.ZodNumber;
    where: z.ZodOptional<z.ZodString>;
    outFields: z.ZodOptional<z.ZodArray<z.ZodString, "many">>;
    geometry: z.ZodOptional<z.ZodRecord<z.ZodString, z.ZodUnknown>>;
    spatialRel: z.ZodOptional<z.ZodEnum<["intersects", "contains", "within"]>>;
    orderBy: z.ZodOptional<z.ZodString>;
    limit: z.ZodOptional<z.ZodNumber>;
    offset: z.ZodOptional<z.ZodNumber>;
    returnGeometry: z.ZodDefault<z.ZodOptional<z.ZodBoolean>>;
}, "strip", z.ZodTypeAny, {
    serviceId: string;
    layerId: number;
    returnGeometry: boolean;
    where?: string | undefined;
    outFields?: string[] | undefined;
    geometry?: Record<string, unknown> | undefined;
    spatialRel?: "intersects" | "contains" | "within" | undefined;
    orderBy?: string | undefined;
    limit?: number | undefined;
    offset?: number | undefined;
}, {
    serviceId: string;
    layerId: number;
    where?: string | undefined;
    outFields?: string[] | undefined;
    geometry?: Record<string, unknown> | undefined;
    spatialRel?: "intersects" | "contains" | "within" | undefined;
    orderBy?: string | undefined;
    limit?: number | undefined;
    offset?: number | undefined;
    returnGeometry?: boolean | undefined;
}>;
export type Input = z.infer<typeof schema>;
export declare function execute(client: HonuaClient, input: Input): Promise<{
    content: Array<{
        type: "text";
        text: string;
    }>;
}>;
//# sourceMappingURL=query-features.d.ts.map