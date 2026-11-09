// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.MapTools;

namespace Honua.Ai.Protocols.Mcp.Tools;

internal static class McpConsoleEntities
{
    private const int MaxEntities = 100;
    private const int MaxIdentityLength = 1024;
    private const int MaxResponseCharacters = 1_048_576;
    private const int MaxResponseDepth = 16;

    internal static void Enrich(string toolName, McpToolsCallResult result)
    {
        if (result.IsError || result.StructuredContent is not { ValueKind: JsonValueKind.Object } payload)
            return;

        var entities = Extract(toolName, payload).Distinct().Take(MaxEntities).ToList();
        if (entities.Count != 0)
            result.Meta = BuildMetadata(result.Meta, entities);
    }

    private static IEnumerable<Entity> Extract(string toolName, JsonElement payload)
    {
        if (toolName == ListLayersTool.ToolName)
            return Layers(payload);
        if (toolName == ListJobsTool.ToolName)
            return Rows(payload, "jobs").SelectMany(job => Identity("job", Text(job, "jobId")));
        if (toolName is ExecutePlanTool.ToolName or GetJobResultsTool.ToolName or CancelJobTool.ToolName)
            return Identity("job", Text(payload, "jobId"));
        if (toolName.StartsWith(PublishedOperationTool.NamePrefix, StringComparison.Ordinal)
            || toolName.StartsWith(PublishedOperationTool.AdminNamePrefix, StringComparison.Ordinal)
            || toolName is PublishServiceTool.ToolName or PublishResultTool.ToolName)
            return Operation(payload);
        return [];
    }

    private static IEnumerable<Entity> Layers(JsonElement payload)
    {
        foreach (var layer in Rows(payload, "layers"))
        {
            var service = Text(layer, "serviceName");
            foreach (var entity in Identity("service", service)) yield return entity;
            if (layer.ValueKind != JsonValueKind.Object
                || !layer.TryGetProperty("layerId", out var index)
                || index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out var layerId) || layerId < 0)
                continue;

            yield return new Entity
            {
                Kind = "layer", Id = layerId.ToString(CultureInfo.InvariantCulture), ServiceName = service
            };
        }
    }

    private static IEnumerable<Entity> Operation(JsonElement payload)
    {
        var status = Text(payload, "status");
        if (status == "RequiresApproval")
            return Identity("approval", Text(payload, "proposalId"));
        if (status != "Completed")
            return [];

        var operation = Text(payload, "operationId");
        var resources = operation switch
        {
            "admin.services.list" or "folder.list" => Catalog(payload, operation),
            "admin.layer.publish" => PublishedLayer(payload),
            _ => []
        };
        return Identity("job", Text(payload, "jobId")).Concat(resources);
    }

    private static IEnumerable<Entity> PublishedLayer(JsonElement payload)
    {
        if (!payload.TryGetProperty("resourceIds", out var resources))
            yield break;

        var service = Text(resources, "serviceName");
        foreach (var entity in Identity("service", service)) yield return entity;
        var layer = Text(resources, "layerId");
        if (int.TryParse(layer, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            yield return new Entity { Kind = "layer", Id = layer!, ServiceName = service };
    }

    private static IEnumerable<Entity> Catalog(JsonElement payload, string operation)
    {
        if (!payload.TryGetProperty("details", out var details))
            return [];
        var response = Text(details, "response", MaxResponseCharacters);
        if (response is null)
            return [];

        try
        {
            using var document = JsonDocument.Parse(response, new JsonDocumentOptions { MaxDepth = MaxResponseDepth });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("data", out var data))
                return [];
            return CatalogData(data, operation).ToArray();
        }
        catch (JsonException)
        {
            // Navigation metadata must not replace the canonical operation result.
            return [];
        }
    }

    private static IEnumerable<Entity> CatalogData(JsonElement data, string operation)
    {
        if (operation == "admin.services.list")
            return Rows(data, "items").SelectMany(service => Identity("service", Text(service, "serviceName")));
        return data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().Take(MaxEntities).SelectMany(Folder)
            : [];
    }

    private static IEnumerable<Entity> Folder(JsonElement folder) =>
        folder.ValueKind == JsonValueKind.Object && folder.TryGetProperty("metadata", out var metadata)
            ? Identity("folder", Text(metadata, "id"))
            : [];

    private static IEnumerable<JsonElement> Rows(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty(name, out var rows) && rows.ValueKind == JsonValueKind.Array
            ? rows.EnumerateArray().Take(MaxEntities)
            : [];

    private static string? Text(JsonElement payload, string name, int maxLength = MaxIdentityLength)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String)
            return null;

        var text = value.GetString();
        return !string.IsNullOrWhiteSpace(text) && text.Length <= maxLength ? text : null;
    }

    private static IEnumerable<Entity> Identity(string kind, string? id) =>
        id is null ? [] : [new Entity { Kind = kind, Id = id }];

    private static JsonElement BuildMetadata(JsonElement? existing, List<Entity> entities)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (existing is { ValueKind: JsonValueKind.Object } metadata)
            {
                foreach (var property in metadata.EnumerateObject())
                    if (property.Name != "honua/consoleEntities") property.WriteTo(writer);
            }
            writer.WriteStartArray("honua/consoleEntities");
            foreach (var entity in entities) WriteEntity(writer, entity);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static void WriteEntity(Utf8JsonWriter writer, Entity entity)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", entity.Kind);
        writer.WriteString("id", entity.Id);
        if (entity.ServiceName is not null) writer.WriteString("serviceName", entity.ServiceName);
        writer.WriteString("href", Href(entity));
        writer.WriteEndObject();
    }

    private static string Href(Entity entity)
    {
        var id = Uri.EscapeDataString(entity.Id);
        return entity.Kind switch
        {
            "folder" => $"/operate/folders/{id}",
            "service" => $"/operate/services/{id}/settings",
            "layer" => $"/operate/layers/{id}",
            "job" => $"/operate/jobs/{id}",
            "approval" => $"/inbox?proposalId={id}",
            _ => throw new InvalidOperationException("Unknown Console entity kind.")
        };
    }

    private sealed record Entity
    {
        public required string Kind { get; init; }
        public required string Id { get; init; }
        public string? ServiceName { get; init; }
    }
}
