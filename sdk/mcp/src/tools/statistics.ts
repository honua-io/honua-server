import { z } from "zod";
import type { HonuaClient } from "@honua/sdk-js";
import { jsonText } from "../helpers.js";

export const schema = z.object({
  serviceId: z.string().describe("The feature service ID"),
  layerId: z.number().int().describe("The layer ID within the service"),
  statisticType: z
    .enum(["count", "sum", "avg", "min", "max", "stddev"])
    .describe("The aggregate function to compute"),
  onField: z.string().describe("The field to compute the statistic on"),
  groupBy: z.string().optional().describe("Field to group results by"),
  where: z.string().optional().describe("SQL WHERE clause to filter features"),
});

export type Input = z.infer<typeof schema>;

export async function execute(client: HonuaClient, input: Input) {
  const outStatisticFieldName = `${input.statisticType}_${input.onField}`;

  const response = await client.queryFeatures({
    serviceId: input.serviceId,
    layerId: input.layerId,
    where: input.where ?? "1=1",
    outStatistics: [
      {
        statisticType: input.statisticType,
        onStatisticField: input.onField,
        outStatisticFieldName,
      },
    ],
    groupByFieldsForStatistics: input.groupBy,
    returnGeometry: false,
  });

  const features = response.features ?? [];

  return jsonText({
    statistics: features.map((f) => ({ attributes: f.attributes })),
  });
}
