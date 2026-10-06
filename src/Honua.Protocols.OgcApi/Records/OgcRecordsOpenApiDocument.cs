// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Honua.Protocols.Ogc.Api.Records;

/// <summary>
/// Builds the OpenAPI 3.0 definition the OGC API Records landing page links as <c>service-desc</c>.
/// </summary>
/// <remarks>
/// honua-server#5510: OGC API - Records Part 1 <c>/req/core/root-success</c> (inherited from OGC API -
/// Common) requires the landing page to link an API definition that describes this API. The document
/// is generated from the same constants the handlers enforce (paging bounds, accepted item query
/// parameters), so the advertised contract cannot drift from runtime behaviour.
/// </remarks>
internal static class OgcRecordsOpenApiDocument
{
    private const string GeoJson = "application/geo+json";
    private const string Json = "application/json";
    private const string ProblemJson = "application/problem+json";

    /// <summary>
    /// Serializes the Records API definition for the given public base URL.
    /// </summary>
    public static string Build(string baseUrl, int defaultLimit, int maxLimit)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("openapi", "3.0.3");

            writer.WriteStartObject("info");
            writer.WriteString("title", "Honua OGC API Records");
            writer.WriteString("description", "Read-only metadata record discovery for Honua catalog services and layers.");
            writer.WriteString("version", "1.0.0");
            writer.WriteEndObject();

            writer.WriteStartArray("servers");
            writer.WriteStartObject();
            writer.WriteString("url", $"{baseUrl}/ogc/records");
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteStartObject("paths");
            WriteMetadataPath(writer, "/", "getLandingPage", "Landing page", Json, hasCollectionId: false);
            WriteMetadataPath(writer, "/openapi.json", "getApiDefinition", "API definition", "application/vnd.oai.openapi+json;version=3.0", hasCollectionId: false);
            WriteMetadataPath(writer, "/conformance", "getConformanceDeclaration", "Conformance declaration", Json, hasCollectionId: false);
            WriteMetadataPath(writer, "/collections", "getCollections", "Record collections", Json, hasCollectionId: false);
            WriteMetadataPath(writer, "/collections/{collectionId}", "describeCollection", "Record collection metadata", Json, hasCollectionId: true);
            WriteItemsPath(writer, defaultLimit, maxLimit);
            WriteItemPath(writer);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteMetadataPath(Utf8JsonWriter writer, string path, string operationId, string summary, string mediaType, bool hasCollectionId)
    {
        writer.WriteStartObject(path);
        writer.WriteStartObject("get");
        writer.WriteString("operationId", operationId);
        writer.WriteString("summary", summary);
        writer.WriteStartArray("parameters");
        if (hasCollectionId)
        {
            WritePathParameter(writer, "collectionId");
        }

        WriteQueryParameter(writer, "f", "Response format.", "string");
        writer.WriteEndArray();
        writer.WriteStartObject("responses");
        WriteResponse(writer, "200", summary, mediaType);
        WriteResponse(writer, "400", "Invalid request.", ProblemJson);
        if (hasCollectionId)
        {
            WriteResponse(writer, "404", "Record collection not found.", ProblemJson);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteItemsPath(Utf8JsonWriter writer, int defaultLimit, int maxLimit)
    {
        writer.WriteStartObject("/collections/{collectionId}/items");
        writer.WriteStartObject("get");
        writer.WriteString("operationId", "getRecords");
        writer.WriteString("summary", "Search records");
        writer.WriteStartArray("parameters");
        WritePathParameter(writer, "collectionId");
        WriteQueryParameter(writer, "f", "Response format (json or geojson).", "string");

        writer.WriteStartObject();
        writer.WriteString("name", "limit");
        writer.WriteString("in", "query");
        writer.WriteString("description", $"Maximum number of records per page. A value above {maxLimit} is served as {maxLimit}.");
        writer.WriteBoolean("required", false);
        writer.WriteStartObject("schema");
        writer.WriteString("type", "integer");
        writer.WriteNumber("minimum", 1);
        writer.WriteNumber("maximum", maxLimit);
        writer.WriteNumber("default", defaultLimit);
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WriteStartObject();
        writer.WriteString("name", "offset");
        writer.WriteString("in", "query");
        writer.WriteString("description", "Number of matching records to skip.");
        writer.WriteBoolean("required", false);
        writer.WriteStartObject("schema");
        writer.WriteString("type", "integer");
        writer.WriteNumber("minimum", 0);
        writer.WriteNumber("default", 0);
        writer.WriteEndObject();
        writer.WriteEndObject();

        WriteQueryParameter(writer, "bbox", "minx,miny,maxx,maxy or minx,miny,minz,maxx,maxy,maxz.", "string");
        WriteQueryParameter(writer, "datetime", "RFC 3339 instant or interval.", "string");
        WriteQueryParameter(writer, "q", "Comma-separated free-text search terms (OR).", "string");
        WriteQueryParameter(writer, "ids", "Comma-separated record ids.", "string");
        WriteQueryParameter(writer, "type", "Comma-separated record types (service, dataset).", "string");
        WriteQueryParameter(writer, "externalIds", "Comma-separated external identifiers.", "string");
        writer.WriteEndArray();

        writer.WriteStartObject("responses");
        WriteResponse(writer, "200", "Records matching the query.", GeoJson);
        WriteResponse(writer, "400", "Invalid query parameter.", ProblemJson);
        WriteResponse(writer, "404", "Record collection not found.", ProblemJson);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteItemPath(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("/collections/{collectionId}/items/{recordId}");
        writer.WriteStartObject("get");
        writer.WriteString("operationId", "getRecord");
        writer.WriteString("summary", "Get a record");
        writer.WriteStartArray("parameters");
        WritePathParameter(writer, "collectionId");
        WritePathParameter(writer, "recordId");
        WriteQueryParameter(writer, "f", "Response format (json or geojson).", "string");
        writer.WriteEndArray();
        writer.WriteStartObject("responses");
        WriteResponse(writer, "200", "The record.", GeoJson);
        WriteResponse(writer, "400", "Invalid request.", ProblemJson);
        WriteResponse(writer, "404", "Record collection or record not found.", ProblemJson);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WritePathParameter(Utf8JsonWriter writer, string name)
    {
        writer.WriteStartObject();
        writer.WriteString("name", name);
        writer.WriteString("in", "path");
        writer.WriteBoolean("required", true);
        writer.WriteStartObject("schema");
        writer.WriteString("type", "string");
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteQueryParameter(Utf8JsonWriter writer, string name, string description, string type)
    {
        writer.WriteStartObject();
        writer.WriteString("name", name);
        writer.WriteString("in", "query");
        writer.WriteString("description", description);
        writer.WriteBoolean("required", false);
        writer.WriteStartObject("schema");
        writer.WriteString("type", type);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteResponse(Utf8JsonWriter writer, string status, string description, string mediaType)
    {
        writer.WriteStartObject(status);
        writer.WriteString("description", description);
        writer.WriteStartObject("content");
        writer.WriteStartObject(mediaType);
        writer.WriteStartObject("schema");
        writer.WriteString("type", "object");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
