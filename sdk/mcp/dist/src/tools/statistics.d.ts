import { z } from "zod";
import type { HonuaClient } from "@honua/sdk-js";
export declare const schema: z.ZodObject<{
    serviceId: z.ZodString;
    layerId: z.ZodNumber;
    statisticType: z.ZodEnum<["count", "sum", "avg", "min", "max", "stddev"]>;
    onField: z.ZodString;
    groupBy: z.ZodOptional<z.ZodString>;
    where: z.ZodOptional<z.ZodString>;
}, "strip", z.ZodTypeAny, {
    serviceId: string;
    layerId: number;
    statisticType: "count" | "sum" | "avg" | "min" | "max" | "stddev";
    onField: string;
    where?: string | undefined;
    groupBy?: string | undefined;
}, {
    serviceId: string;
    layerId: number;
    statisticType: "count" | "sum" | "avg" | "min" | "max" | "stddev";
    onField: string;
    where?: string | undefined;
    groupBy?: string | undefined;
}>;
export type Input = z.infer<typeof schema>;
export declare function execute(client: HonuaClient, input: Input): Promise<{
    content: Array<{
        type: "text";
        text: string;
    }>;
}>;
//# sourceMappingURL=statistics.d.ts.map