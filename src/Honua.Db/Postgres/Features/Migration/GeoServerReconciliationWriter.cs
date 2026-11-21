// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.Migration.Domain;

namespace Honua.Db.Postgres.Features.Migration;

internal sealed record GeoServerReconciliationBuildInput
{
    public required string RunId { get; init; }
    public required string JobId { get; init; }
    public required GeoServerServiceInfo ServiceInfo { get; init; }
    public required GeoServerLayerInfo[] Layers { get; init; }
    public int WorkspaceCount { get; init; }
    public int DataStoreCount { get; init; }
    public int StyleCount { get; init; }
    public required MigrationApplyPlanArtifact ApplyPlan { get; init; }
    public required MigrationApplyExecutionArtifact ApplyExecution { get; init; }
}

internal static class GeoServerReconciliationWriter
{
    private static readonly string[] SupportedMetadataKeys = ["sourceKind", "sourceVersion", "targetService"];

    public static GeoServerReconciliationBundle BuildBundle(GeoServerReconciliationBuildInput input)
    {
        var planId = $"geoserver-plan-{input.JobId}";
        var planDigest = input.ApplyPlan.PlanFingerprint;
        var appliedSteps = input.ApplyExecution.StepResults
            .Where(static s => s.Outcome is "applied" or "already-applied")
            .ToArray();

        var (sourceSchemas, targetSchemas, sourceExtents, targetExtents) = BuildLayerSnapshotMaps(input.Layers, appliedSteps);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sourceKind"] = "geoserver-rest",
            ["sourceVersion"] = input.ServiceInfo.Version ?? string.Empty,
            ["targetService"] = input.ServiceInfo.GlobalSettings?.Title ?? "geoserver-import"
        };

        return new GeoServerReconciliationBundle
        {
            RunId = input.RunId,
            PlanId = planId,
            PlanDigest = planDigest,
            Service = "geoserver",
            SourceSnapshot = CreateSnapshot(new SnapshotPart(input.RunId, planId, planDigest), BuildSourceCounts(input), new SnapshotDetails(sourceSchemas, sourceExtents, metadata)),
            TargetSnapshot = CreateSnapshot(new SnapshotPart(input.RunId, planId, planDigest), BuildTargetCounts(appliedSteps), new SnapshotDetails(targetSchemas, targetExtents, metadata))
        };
    }

    private sealed record SnapshotPart(string RunId, string PlanId, string PlanDigest);

    private sealed record SnapshotDetails(
        Dictionary<string, string[]> Schemas,
        Dictionary<string, GeoServerReconciliationExtent> Extents,
        Dictionary<string, string> Metadata);

    private static GeoServerReconciliationSnapshot CreateSnapshot(
        SnapshotPart id,
        Dictionary<string, int> counts,
        SnapshotDetails details)
        => new()
        {
            RunId = id.RunId,
            PlanId = id.PlanId,
            PlanDigest = id.PlanDigest,
            Service = "geoserver",
            Counts = counts,
            Schemas = details.Schemas,
            Extents = details.Extents,
            Metadata = details.Metadata,
            SupportedMetadata = SupportedMetadataKeys
        };

    public static string SerializeBundle(GeoServerReconciliationBundle bundle)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("run_id", bundle.RunId);
            writer.WriteString("plan_id", bundle.PlanId);
            writer.WriteString("plan_digest", bundle.PlanDigest);
            writer.WriteString("service", bundle.Service);
            writer.WritePropertyName("source_snapshot");
            WriteSnapshot(writer, bundle.SourceSnapshot);
            writer.WritePropertyName("target_snapshot");
            WriteSnapshot(writer, bundle.TargetSnapshot);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static Dictionary<string, int> BuildSourceCounts(GeoServerReconciliationBuildInput input)
        => new(StringComparer.Ordinal)
        {
            ["workspaces"] = input.WorkspaceCount,
            ["datastores"] = input.DataStoreCount,
            ["layers"] = input.Layers.Length,
            ["styles"] = input.StyleCount
        };

    private static Dictionary<string, int> BuildTargetCounts(MigrationApplyExecutionStepResult[] appliedSteps)
        => new(StringComparer.Ordinal)
        {
            ["workspaces"] = appliedSteps.Count(static s => string.Equals(s.Kind, "workspace", StringComparison.Ordinal)),
            ["datastores"] = appliedSteps.Count(static s => string.Equals(s.Kind, "datastore", StringComparison.Ordinal)),
            ["layers"] = appliedSteps.Count(static s => string.Equals(s.Kind, "layer", StringComparison.Ordinal)),
            ["styles"] = appliedSteps.Count(static s => string.Equals(s.Kind, "style", StringComparison.Ordinal))
        };

    private static (
        Dictionary<string, string[]> SourceSchemas,
        Dictionary<string, string[]> TargetSchemas,
        Dictionary<string, GeoServerReconciliationExtent> SourceExtents,
        Dictionary<string, GeoServerReconciliationExtent> TargetExtents)
        BuildLayerSnapshotMaps(GeoServerLayerInfo[] layers, MigrationApplyExecutionStepResult[] appliedSteps)
    {
        var sourceSchemas = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var targetSchemas = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var sourceExtents = new Dictionary<string, GeoServerReconciliationExtent>(StringComparer.Ordinal);
        var targetExtents = new Dictionary<string, GeoServerReconciliationExtent>(StringComparer.Ordinal);
        var appliedLayerIds = appliedSteps
            .Where(static s => string.Equals(s.Kind, "layer", StringComparison.Ordinal))
            .Select(static s => s.SourceId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var layer in layers.OrderBy(static l => $"layer:{l.WorkspaceName}:{l.Name}", StringComparer.Ordinal))
        {
            var layerId = $"layer:{layer.WorkspaceName}:{layer.Name}";
            var schemaSummary = BuildLayerDimensionSummary(layer);
            sourceSchemas[layerId] = schemaSummary;

            var extent = ToReconciliationExtent(layer.LatLonBoundingBox);
            if (extent is not null)
            {
                sourceExtents[layerId] = extent;
            }

            if (appliedLayerIds.Contains(layerId))
            {
                targetSchemas[layerId] = schemaSummary;
                if (extent is not null)
                {
                    targetExtents[layerId] = extent;
                }
            }
        }

        return (sourceSchemas, targetSchemas, sourceExtents, targetExtents);
    }

    private static string[] BuildLayerDimensionSummary(GeoServerLayerInfo layer)
    {
        var dimensions = new List<string>();
        if (layer.TimeDimension is { Enabled: true } td && !string.IsNullOrWhiteSpace(td.Attribute))
        {
            dimensions.Add($"time:{td.Attribute}");
        }

        if (layer.ElevationDimension is { Enabled: true } ed)
        {
            dimensions.Add($"elevation:{ed.Attribute ?? "declared"}");
        }

        return [.. dimensions];
    }

    private static GeoServerReconciliationExtent? ToReconciliationExtent(GeoServerBoundingBox? bbox)
        => bbox is null
            ? null
            : new GeoServerReconciliationExtent
            {
                MinX = bbox.MinX,
                MinY = bbox.MinY,
                MaxX = bbox.MaxX,
                MaxY = bbox.MaxY,
                Crs = bbox.CRS
            };

    private static void WriteSnapshot(Utf8JsonWriter writer, GeoServerReconciliationSnapshot snapshot)
    {
        writer.WriteStartObject();
        writer.WriteString("run_id", snapshot.RunId);
        writer.WriteString("plan_id", snapshot.PlanId);
        writer.WriteString("plan_digest", snapshot.PlanDigest);
        writer.WriteString("service", snapshot.Service);
        WriteCounts(writer, snapshot.Counts);
        WriteSchemas(writer, snapshot.Schemas);
        WriteExtents(writer, snapshot.Extents);
        WriteStringList(writer, "samples", snapshot.Samples);
        WriteStringList(writer, "relationships", snapshot.Relationships);
        WriteMetadata(writer, snapshot.Metadata);
        WriteStringList(writer, "supported_metadata", snapshot.SupportedMetadata);
        writer.WriteEndObject();
    }

    private static void WriteCounts(Utf8JsonWriter writer, IReadOnlyDictionary<string, int> counts)
    {
        writer.WriteStartObject("counts");
        foreach (var (key, value) in counts.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            writer.WriteNumber(key, value);
        }

        writer.WriteEndObject();
    }

    private static void WriteSchemas(Utf8JsonWriter writer, IReadOnlyDictionary<string, string[]> schemas)
    {
        writer.WriteStartObject("schemas");
        foreach (var (key, values) in schemas.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            WriteStringList(writer, key, values);
        }

        writer.WriteEndObject();
    }

    private static void WriteExtents(Utf8JsonWriter writer, IReadOnlyDictionary<string, GeoServerReconciliationExtent> extents)
    {
        writer.WriteStartObject("extents");
        foreach (var (key, extent) in extents.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            writer.WriteStartObject(key);
            writer.WriteNumber("minX", extent.MinX);
            writer.WriteNumber("minY", extent.MinY);
            writer.WriteNumber("maxX", extent.MaxX);
            writer.WriteNumber("maxY", extent.MaxY);
            if (extent.Crs is not null)
            {
                writer.WriteString("crs", extent.Crs);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteMetadata(Utf8JsonWriter writer, IReadOnlyDictionary<string, string> metadata)
    {
        writer.WriteStartObject("metadata");
        foreach (var (key, value) in metadata.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            writer.WriteString(key, value);
        }

        writer.WriteEndObject();
    }

    private static void WriteStringList(Utf8JsonWriter writer, string propertyName, IEnumerable<string> values)
    {
        writer.WriteStartArray(propertyName);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}
