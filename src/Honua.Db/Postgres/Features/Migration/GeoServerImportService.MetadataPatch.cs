// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.Security.Domain;

namespace Honua.Db.Postgres.Features.Migration;

internal sealed partial class GeoServerImportService
{
    private sealed record DryRunCompletionContext
    {
        public required string JobId { get; init; }
        public required GeoServerServiceInfo ServiceInfo { get; init; }
        public required FilteredResources FilteredResources { get; init; }
        public required GeoServerImportRequest Request { get; init; }
        public required GeoServerImportProgress CurrentProgress { get; init; }
        public IProgress<GeoServerImportProgress>? Progress { get; init; }
        public required MigrationRunMetricsRecorder MetricsRecorder { get; init; }
        public required TimeSpan Elapsed { get; init; }
    }

    private async Task<GeoServerImportResult> CompleteDryRunAsync(
        DryRunCompletionContext context,
        CancellationToken cancellationToken)
    {
        MigrationApplyPlanArtifact dryRunApplyPlan;
        using (context.MetricsRecorder.BeginPhase(MigrationCostPerformancePhases.Manifest))
        {
            dryRunApplyPlan = await CreateApplyPlanAsync(context.ServiceInfo, context.FilteredResources, context.Request, cancellationToken).ConfigureAwait(false);
            context.MetricsRecorder.RecordResourceCount(dryRunApplyPlan.Summary.TotalStepCount);
            context.MetricsRecorder.RecordManualReview(dryRunApplyPlan.Summary.ManualReviewStepCount, dryRunApplyPlan.Summary.TotalStepCount);
            context.MetricsRecorder.SampleResources();
        }

        var dryRunMetrics = BuildRunMetricsArtifact(context.MetricsRecorder, context.ServiceInfo, context.JobId, "geoserver dry run");
        EmitRunMetrics(dryRunMetrics);
        context.Progress?.Report(context.CurrentProgress with
        {
            Status = GeoServerImportStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Dry run completed",
            ApplyPlan = dryRunApplyPlan,
            RunMetrics = dryRunMetrics
        });

        return CreateDryRunResult(context.ServiceInfo, context.FilteredResources, context.Request, context.Elapsed) with
        {
            ApplyPlan = dryRunApplyPlan,
            RunMetrics = dryRunMetrics
        };
    }

    private enum WmsDimensionKind
    {
        Time,
        Elevation
    }

    private readonly record struct LayerMetadataTarget(
        int StorageLayerId,
        GeoServerLayerInfo Layer,
        AccessPolicy? AccessPolicy);

    private sealed record LayerMetadataPatch
    {
        public required int StorageLayerId { get; init; }
        public MetadataV2ObjectTemporal? Temporal { get; init; }
        public JsonElement? WmsDimensions { get; init; }
        public AccessPolicy? AccessPolicy { get; init; }
    }

    private readonly record struct WmsDimensionSpec(
        string Name,
        GeoServerDimensionInfo Dimension,
        WmsDimensionKind Kind);

    private async Task ApplyLayerDimensionsAndAccessPolicyAsync(
        LayerMetadataTarget target,
        CancellationToken cancellationToken)
    {
        if (MetadataGraphStore == null)
        {
            return;
        }

        var temporal = BuildLayerTemporal(target.Layer.TimeDimension);
        var wmsDimensions = BuildWmsDimensionsExtension(target.Layer.TimeDimension, target.Layer.ElevationDimension);
        if (temporal == null && wmsDimensions == null && target.AccessPolicy == null)
        {
            return;
        }

        var patch = new LayerMetadataPatch
        {
            StorageLayerId = target.StorageLayerId,
            Temporal = temporal,
            WmsDimensions = wmsDimensions,
            AccessPolicy = target.AccessPolicy
        };
        await SaveLayerMetadataPatchAsync(patch, cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveLayerMetadataPatchAsync(LayerMetadataPatch patch, CancellationToken cancellationToken)
    {
        const int maxAttempts = 2;
        var resourceId = $"res-layer-{patch.StorageLayerId.ToString(CultureInfo.InvariantCulture)}";

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await MetadataGraphStore!.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var resources = snapshot.Graph.Resources.ToList();
            var index = resources.FindIndex(r => string.Equals(r.Metadata.Id, resourceId, StringComparison.Ordinal));
            if (index < 0)
            {
                return;
            }

            var current = resources[index];
            resources[index] = current with
            {
                Temporal = patch.Temporal ?? current.Temporal,
                AccessPolicy = patch.AccessPolicy ?? current.AccessPolicy,
                Extensions = MergeWmsExtension(current.Extensions, patch.WmsDimensions)
            };

            try
            {
                _ = await MetadataGraphStore.SaveAsync(
                        snapshot.Graph with { Resources = resources },
                        snapshot.Etag,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (MetadataV2GraphConcurrencyException) when (attempt < maxAttempts)
            {
                // Retry once on optimistic concurrency conflict.
            }
        }
    }

    internal static MetadataV2ObjectTemporal? BuildLayerTemporal(GeoServerDimensionInfo? timeDimension)
    {
        if (timeDimension is not { Enabled: true })
        {
            return null;
        }

        var hasField = !string.IsNullOrWhiteSpace(timeDimension.Attribute) ||
            !string.IsNullOrWhiteSpace(timeDimension.EndAttribute);
        var hasExtent = timeDimension.StartTime.HasValue || timeDimension.EndTime.HasValue;
        if (!hasField && !hasExtent)
        {
            return null;
        }

        return new MetadataV2ObjectTemporal
        {
            StartTimeField = string.IsNullOrWhiteSpace(timeDimension.Attribute) ? null : timeDimension.Attribute,
            EndTimeField = string.IsNullOrWhiteSpace(timeDimension.EndAttribute) ? null : timeDimension.EndAttribute,
            Extent = hasExtent
                ? new MetadataV2TimeRange { Start = timeDimension.StartTime, End = timeDimension.EndTime }
                : null
        };
    }

    internal static JsonElement? BuildWmsDimensionsExtension(
        GeoServerDimensionInfo? timeDimension,
        GeoServerDimensionInfo? elevationDimension)
    {
        var specs = new List<WmsDimensionSpec>(2);
        if (timeDimension is { Enabled: true } &&
            string.IsNullOrWhiteSpace(timeDimension.Attribute) &&
            !string.IsNullOrWhiteSpace(FormatDimensionExtent(timeDimension, WmsDimensionKind.Time)))
        {
            specs.Add(new WmsDimensionSpec("time", timeDimension, WmsDimensionKind.Time));
        }

        if (elevationDimension is { Enabled: true } &&
            !string.IsNullOrWhiteSpace(FormatDimensionExtent(elevationDimension, WmsDimensionKind.Elevation)))
        {
            specs.Add(new WmsDimensionSpec("elevation", elevationDimension, WmsDimensionKind.Elevation));
        }

        if (specs.Count == 0)
        {
            return null;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("dimensions");
            foreach (var spec in specs)
            {
                WriteWmsDimensionEntry(writer, spec);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    private static void WriteWmsDimensionEntry(Utf8JsonWriter writer, WmsDimensionSpec spec)
    {
        var extent = FormatDimensionExtent(spec.Dimension, spec.Kind) ?? string.Empty;
        var units = !string.IsNullOrWhiteSpace(spec.Dimension.Units)
            ? spec.Dimension.Units
            : (spec.Kind == WmsDimensionKind.Time ? "ISO8601" : "EPSG:5030");

        writer.WriteStartObject();
        writer.WriteString("name", spec.Name);
        writer.WriteString("units", units);
        if (!string.IsNullOrWhiteSpace(spec.Dimension.UnitSymbol))
        {
            writer.WriteString("unitSymbol", spec.Dimension.UnitSymbol);
        }

        writer.WriteBoolean("multipleValues", false);
        writer.WriteBoolean("nearestValue", spec.Dimension.NearestMatchEnabled);

        var defaultValue = ResolveDimensionDefault(spec.Dimension, spec.Kind);
        if (!string.IsNullOrWhiteSpace(defaultValue))
        {
            writer.WriteString("default", defaultValue);
        }

        writer.WriteString("extent", extent);
        writer.WriteEndObject();
    }

    private static string? FormatDimensionExtent(GeoServerDimensionInfo dimension, WmsDimensionKind kind)
    {
        if (dimension.Values.Length > 0)
        {
            return string.Join(",", dimension.Values);
        }

        var isTime = kind == WmsDimensionKind.Time;
        var start = isTime && dimension.StartTime.HasValue
            ? FormatUtcIso(dimension.StartTime.Value)
            : dimension.StartValue;
        var end = isTime && dimension.EndTime.HasValue
            ? FormatUtcIso(dimension.EndTime.Value)
            : dimension.EndValue;

        if (string.IsNullOrWhiteSpace(start) || string.IsNullOrWhiteSpace(end))
        {
            return start ?? end;
        }

        return $"{start}/{end}/{FormatDimensionResolution(dimension, kind)}";
    }

    private static string FormatDimensionResolution(GeoServerDimensionInfo dimension, WmsDimensionKind kind)
    {
        if (string.Equals(dimension.Presentation, "DISCRETE_INTERVAL", StringComparison.OrdinalIgnoreCase) &&
            dimension.Resolution is > 0)
        {
            return kind == WmsDimensionKind.Time
                ? FormatIsoDurationFromMilliseconds(dimension.Resolution.Value)
                : dimension.Resolution.Value.ToString(CultureInfo.InvariantCulture);
        }

        return "0";
    }

    private static string FormatIsoDurationFromMilliseconds(long milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(milliseconds);
        if (span.TotalDays >= 1 && span.Hours == 0 && span.Minutes == 0 && span.Seconds == 0 && span.Milliseconds == 0)
        {
            return $"P{((long)span.TotalDays).ToString(CultureInfo.InvariantCulture)}D";
        }

        if (span.TotalHours >= 1 && span.Minutes == 0 && span.Seconds == 0 && span.Milliseconds == 0)
        {
            return $"PT{((long)span.TotalHours).ToString(CultureInfo.InvariantCulture)}H";
        }

        if (span.TotalMinutes >= 1 && span.Seconds == 0 && span.Milliseconds == 0)
        {
            return $"PT{((long)span.TotalMinutes).ToString(CultureInfo.InvariantCulture)}M";
        }

        return $"PT{((long)Math.Max(1, Math.Round(span.TotalSeconds))).ToString(CultureInfo.InvariantCulture)}S";
    }

    private static string? ResolveDimensionDefault(GeoServerDimensionInfo dimension, WmsDimensionKind kind)
    {
        if (!string.IsNullOrWhiteSpace(dimension.DefaultReferenceValue))
        {
            return dimension.DefaultReferenceValue;
        }

        var isTime = kind == WmsDimensionKind.Time;
        if (string.Equals(dimension.DefaultValueStrategy, "MINIMUM", StringComparison.OrdinalIgnoreCase))
        {
            return isTime && dimension.StartTime.HasValue
                ? FormatUtcIso(dimension.StartTime.Value)
                : dimension.StartValue ?? dimension.Values.FirstOrDefault();
        }

        if (string.Equals(dimension.DefaultValueStrategy, "MAXIMUM", StringComparison.OrdinalIgnoreCase))
        {
            return isTime && dimension.EndTime.HasValue
                ? FormatUtcIso(dimension.EndTime.Value)
                : dimension.EndValue ?? dimension.Values.LastOrDefault();
        }

        return null;
    }

    private static string FormatUtcIso(DateTimeOffset timestamp)
        => timestamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static IReadOnlyDictionary<string, JsonElement> MergeWmsExtension(
        IReadOnlyDictionary<string, JsonElement> existing,
        JsonElement? wmsExtension)
    {
        if (wmsExtension is null)
        {
            return existing;
        }

        var mergedValue = existing.TryGetValue("wms", out var currentWms) &&
            currentWms.ValueKind == JsonValueKind.Object
            ? MergeJsonObjects(currentWms, wmsExtension.Value)
            : wmsExtension.Value;

        return new Dictionary<string, JsonElement>(existing, StringComparer.Ordinal)
        {
            ["wms"] = mergedValue
        };
    }

    private static JsonElement MergeJsonObjects(JsonElement current, JsonElement incoming)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in current.EnumerateObject())
            {
                if (!incoming.TryGetProperty(property.Name, out _))
                {
                    property.WriteTo(writer);
                }
            }

            foreach (var property in incoming.EnumerateObject())
            {
                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }
}
