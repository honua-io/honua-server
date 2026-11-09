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
    private const int MaxResponseLength = 1_048_576;

    internal static void Enrich(string toolName, McpToolsCallResult result)
    {
        if (result.IsError || result.StructuredContent is not { ValueKind: JsonValueKind.Object } payload)
            return;

        var entities = new List<Entity>();
        if (toolName == ListLayersTool.ToolName)
            AddLayers(payload, entities);
        else if (toolName == ListJobsTool.ToolName)
            AddJobs(payload, entities);
        else if (toolName is ExecutePlanTool.ToolName or GetJobResultsTool.ToolName or CancelJobTool.ToolName)
            Add(entities, "job", Text(payload, "jobId"));
        else if (toolName.StartsWith(PublishedOperationTool.NamePrefix, StringComparison.Ordinal)
            || toolName.StartsWith(PublishedOperationTool.AdminNamePrefix, StringComparison.Ordinal)
            || toolName is PublishServiceTool.ToolName or PublishResultTool.ToolName)
            AddOperation(payload, entities);

        if (entities.Count != 0)
            result.Meta = BuildMetadata(result.Meta, entities);
    }

    private static void AddLayers(JsonElement payload, List<Entity> entities)
    {
        foreach (var layer in Rows(payload, "layers"))
        {
            var service = Text(layer, "serviceName");
            Add(entities, "service", service);
            if (layer.ValueKind != JsonValueKind.Object
                || !layer.TryGetProperty("layerId", out var index)
                || index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out var layerId) || layerId < 0)
                continue;

            AddLayer(entities, layerId.ToString(CultureInfo.InvariantCulture), service);
        }
    }

    private static void AddJobs(JsonElement payload, List<Entity> entities)
    {
        foreach (var job in Rows(payload, "jobs"))
            Add(entities, "job", Text(job, "jobId"));
    }

    private static void AddOperation(JsonElement payload, List<Entity> entities)
    {
        var status = Text(payload, "status");
        if (status == "RequiresApproval")
        {
            Add(entities, "approval", Text(payload, "proposalId"));
            return;
        }

        if (status != "Completed")
            return;

        Add(entities, "job", Text(payload, "jobId"));
        var operation = Text(payload, "operationId");
        if (operation is "admin.services.list" or "folder.list" or "folder.create" or "folder.update")
            AddCatalog(payload, operation, entities);
        else if (operation == "admin.layer.publish")
            AddPublishedLayer(payload, entities);
    }

    private static void AddPublishedLayer(JsonElement payload, List<Entity> entities)
    {
        if (!payload.TryGetProperty("resourceIds", out var resources))
            return;

        var service = Text(resources, "serviceName");
        var layer = Text(resources, "layerId");
        if (!int.TryParse(layer, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            return;

        Add(entities, "service", service);
        AddLayer(entities, layer, service);
    }

    private static void AddCatalog(JsonElement payload, string operation, List<Entity> entities)
    {
        if (!payload.TryGetProperty("details", out var details))
            return;

        var response = Text(details, "response", MaxResponseLength);
        if (response is null)
            return;

        try
        {
            using var document = JsonDocument.Parse(response, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("data", out var data))
                return;

            AddCatalogData(data, operation, entities);
        }
        catch (JsonException)
        {
            // Navigation metadata must not replace the canonical operation result.
        }
    }

    private static void AddCatalogData(JsonElement data, string operation, List<Entity> entities)
    {
        if (operation == "admin.services.list")
        {
            foreach (var service in Rows(data, "items"))
                Add(entities, "service", Text(service, "serviceName"));
            return;
        }

        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var folder in data.EnumerateArray().Take(MaxEntities))
                AddFolder(folder, entities);
        }
        else
            AddFolder(data, entities);
    }

    private static void AddFolder(JsonElement folder, List<Entity> entities)
    {
        if (folder.ValueKind == JsonValueKind.Object && folder.TryGetProperty("metadata", out var metadata))
            Add(entities, "folder", Text(metadata, "id"));
    }

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

    private static void Add(List<Entity> entities, string kind, string? id)
    {
        if (id is not null)
            AddEntity(entities, new Entity { Kind = kind, Id = id });
    }

    private static void AddLayer(List<Entity> entities, string id, string? serviceName) =>
        AddEntity(entities, new Entity { Kind = "layer", Id = id, ServiceName = serviceName });

    private static void AddEntity(List<Entity> entities, Entity entity)
    {
        if (entities.Count < MaxEntities && !entities.Contains(entity))
            entities.Add(entity);
    }

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
