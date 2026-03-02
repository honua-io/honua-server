import { z } from "zod";
import type { HonuaClient } from "@honua/sdk-js";
export declare const schema: z.ZodObject<{
    includeDetails: z.ZodDefault<z.ZodOptional<z.ZodBoolean>>;
}, "strip", z.ZodTypeAny, {
    includeDetails: boolean;
}, {
    includeDetails?: boolean | undefined;
}>;
export type Input = z.infer<typeof schema>;
export declare function execute(client: HonuaClient, input: Input): Promise<{
    content: Array<{
        type: "text";
        text: string;
    }>;
}>;
//# sourceMappingURL=list-services.d.ts.map