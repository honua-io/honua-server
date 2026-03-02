import { decodeServiceId, parseLayerId } from "../helpers.js";
export const uriTemplate = "honua://services/{encodedServiceId}/layers/{layerId}";
export async function read(client, encodedServiceId, layerId) {
    const serviceId = decodeServiceId(encodedServiceId);
    const layerIdNum = parseLayerId(layerId);
    const meta = await client.getLayerMetadata(serviceId, layerIdNum);
    const schema = {
        id: meta.id,
        name: meta.name,
        description: meta.description ?? null,
        geometryType: meta.geometryType ?? null,
        fields: (meta.fields ?? []).map((f) => ({ name: f.name, type: f.type, alias: f.alias ?? f.name })),
        extent: meta.extent ?? null,
        spatialReference: meta.spatialReference ?? null,
        relationships: meta.relationships ?? [],
    };
    const uri = `honua://services/${encodedServiceId}/layers/${layerId}`;
    return {
        contents: [
            {
                uri,
                mimeType: "application/json",
                text: JSON.stringify(schema, null, 2),
            },
        ],
    };
}
