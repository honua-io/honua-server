import { describe, it, expect, vi } from "vitest";
import { execute, schema } from "../../src/tools/statistics.js";
import { createMockClient, asClient } from "../test-helpers.js";
describe("honua_statistics", () => {
    it("builds outStatistics and returns grouped results", async () => {
        const mock = createMockClient({
            queryFeatures: vi.fn().mockResolvedValue({
                features: [
                    { attributes: { STATE: "CA", avg_VALUE: 150 } },
                    { attributes: { STATE: "NY", avg_VALUE: 200 } },
                ],
            }),
        });
        const result = await execute(asClient(mock), schema.parse({
            serviceId: "Census",
            layerId: 0,
            statisticType: "avg",
            onField: "VALUE",
            groupBy: "STATE",
        }));
        const parsed = JSON.parse(result.content[0].text);
        expect(parsed.statistics).toHaveLength(2);
        expect(parsed.statistics[0].attributes.STATE).toBe("CA");
    });
    it("passes correct outStatistics to queryFeatures", async () => {
        const mock = createMockClient();
        await execute(asClient(mock), schema.parse({
            serviceId: "Parks",
            layerId: 0,
            statisticType: "sum",
            onField: "AREA",
        }));
        expect(mock.queryFeatures).toHaveBeenCalledWith(expect.objectContaining({
            outStatistics: [
                {
                    statisticType: "sum",
                    onStatisticField: "AREA",
                    outStatisticFieldName: "sum_AREA",
                },
            ],
            returnGeometry: false,
        }));
    });
    it("passes where filter and groupBy", async () => {
        const mock = createMockClient();
        await execute(asClient(mock), schema.parse({
            serviceId: "Parks",
            layerId: 0,
            statisticType: "count",
            onField: "OBJECTID",
            where: "STATE = 'CA'",
            groupBy: "COUNTY",
        }));
        expect(mock.queryFeatures).toHaveBeenCalledWith(expect.objectContaining({
            where: "STATE = 'CA'",
            groupByFieldsForStatistics: "COUNTY",
        }));
    });
});
