// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

/// <summary>
/// Canonical Esri GP task-name, schema, and submission projection shared by
/// GeoServices GPServer and AI/MCP. This is a presentation adapter over the
/// built-in process catalog and <see cref="IGeoprocessingJobService"/>; it does
/// not define a second process registry or execution engine.
/// </summary>
internal static class EsriGpTaskProjection
{
    private static readonly FrozenDictionary<string, string> AliasByProcessId =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["geometry.buffer"] = "Buffer",
            ["geometry.snap"] = "Snap",
            ["overlay.clip"] = "Clip",
            ["overlay.intersect"] = "Intersect",
            ["overlay.union"] = "Union",
            ["overlay.erase"] = "Erase",
            ["overlay.merge"] = "Merge",
            ["overlay.split"] = "Split",
            ["proximity.near"] = "Near",
            ["proximity.near-table"] = "GenerateNearTable",
            ["proximity.euclidean-distance"] = "EucDistance",
            ["proximity.euclidean-allocation"] = "EucAllocation",
            ["statistics.summarize"] = "Statistics",
            ["statistics.frequency"] = "Frequency",
            ["surface.slope"] = "Slope",
            ["surface.aspect"] = "Aspect",
            ["surface.hillshade"] = "Hillshade",
            ["surface.contour"] = "Contour",
            ["surface.viewshed"] = "Viewshed",
            ["raster.reproject"] = "ProjectRaster",
            ["raster.statistics"] = "CalculateStatistics",
            ["raster.zonal-statistics"] = "ZonalStatisticsAsTable",
            ["raster.resample"] = "Resample",
            ["raster.interpolate-idw"] = "IDW",
            ["raster.interpolate-kriging"] = "Kriging",
            ["raster.mosaic"] = "MosaicToNewRaster",
            ["raster.reclassify"] = "Reclassify",
            ["imagery.classify"] = "ClassifyRaster",
            ["conversion.feature-project"] = "Project",
            ["conversion.polygonize"] = "RasterToPolygon",
            ["conversion.rasterize"] = "FeatureToRaster",
            ["data-management.copy-features"] = "CopyFeatures",
            ["data-management.append"] = "Append",
            ["data-management.delete-features"] = "DeleteFeatures",
            ["data-management.calculate-field"] = "CalculateField",
            ["generalization.dissolve"] = "Dissolve",
            ["analytics.spatial-join-managed"] = "SpatialJoin",
            ["analytics.hotspot-managed"] = "HotSpots",
            ["enrichment.enrich"] = "EnrichLayer",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, string> ProcessIdByAlias =
        AliasByProcessId
            .ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static string? GetAlias(string processId)
        => AliasByProcessId.GetValueOrDefault(processId);

    public static bool TryResolveProcessId(string taskName, out string processId)
        => ProcessIdByAlias.TryGetValue(taskName, out processId!);

    public static IReadOnlyList<EsriGpTaskSummary> ListTasks(IProcessCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var processes = catalog.ListProcesses();
        var processIds = processes
            .Select(process => process.ProcessId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tasks = new List<EsriGpTaskSummary>(processes.Count + AliasByProcessId.Count);

        foreach (var process in processes.OrderBy(process => process.ProcessId, StringComparer.Ordinal))
        {
            tasks.Add(ToSummary(process.ProcessId, process, isAlias: false));
            var alias = GetAlias(process.ProcessId);
            if (alias != null && !processIds.Contains(alias))
            {
                tasks.Add(ToSummary(alias, process, isAlias: true));
            }
        }

        return tasks;
    }

    public static ProcessDefinition? ResolveTask(IProcessCatalog catalog, string? taskName)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (string.IsNullOrWhiteSpace(taskName))
        {
            return null;
        }

        var byProcessId = catalog.GetProcess(taskName);
        if (byProcessId != null)
        {
            return byProcessId;
        }

        if (!TryResolveProcessId(taskName, out var processId)
            || catalog.ListProcesses().Any(process =>
                string.Equals(process.ProcessId, taskName, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return catalog.GetProcess(processId);
    }

    public static EsriGpTaskDescription? DescribeTask(IProcessCatalog catalog, string taskName)
    {
        var definition = ResolveTask(catalog, taskName);
        return definition == null ? null : DescribeTask(taskName, definition);
    }

    public static EsriGpTaskDescription DescribeTask(string taskName, ProcessDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        ArgumentNullException.ThrowIfNull(definition);
        var parameters = new List<EsriGpTaskParameter>(
            definition.Parameters.Count + definition.OutputArtifactKinds.Count);
        parameters.AddRange(definition.Parameters.Select(parameter => new EsriGpTaskParameter(
            parameter.Name,
            parameter.DisplayName,
            parameter.Description,
            ToEsriDataType(parameter.ValueType),
            "esriGPParameterDirectionInput",
            parameter.Required ? "esriGPParameterTypeRequired" : "esriGPParameterTypeOptional",
            parameter.DefaultValue,
            parameter.AllowedValues)));

        for (var index = 0; index < definition.OutputArtifactKinds.Count; index++)
        {
            var kind = definition.OutputArtifactKinds[index];
            var outputName = BuildOutputParameterName(kind, index, definition.OutputArtifactKinds);
            parameters.Add(new EsriGpTaskParameter(
                outputName,
                outputName,
                $"Output artifact of type {kind}.",
                ToEsriDataType(kind),
                "esriGPParameterDirectionOutput",
                definition.OutputsAreAlternatives
                    ? "esriGPParameterTypeOptional"
                    : "esriGPParameterTypeRequired",
                DefaultValue: null,
                AllowedValues: null));
        }

        return new EsriGpTaskDescription(
            taskName,
            definition.ProcessId,
            definition.Title,
            definition.Description,
            definition.Category,
            "esriExecutionTypeAsynchronous",
            GeoprocessingSynchronousExecutionPolicy.IsSynchronous(definition),
            parameters);
    }

    public static AnalysisPlan BuildSubmissionPlan(
        ProcessDefinition definition,
        string serviceId,
        IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentNullException.ThrowIfNull(parameters);

        var taskSlug = definition.ProcessId.Replace(".", "-", StringComparison.Ordinal);
        return new AnalysisPlan
        {
            PlanId = $"gpserver-{serviceId}-{taskSlug}-{Guid.NewGuid():N}",
            IntentId = $"gpserver:{serviceId}:{definition.ProcessId}",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = $"gp-task-{taskSlug}",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = definition.ProcessId,
                    Inputs = TranslateInbound(parameters, definition)
                }
            ],
            Outputs = definition.OutputArtifactKinds
        };
    }

    public static Dictionary<string, string> BuildProtocolMetadata(
        string serviceId,
        string taskName,
        ProcessDefinition definition)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["submittedVia"] = "GPServer",
            [GeoprocessingProtocolMetadataKeys.GPServerServiceId] = serviceId,
            [GeoprocessingProtocolMetadataKeys.GPServerTaskName] = taskName
        };

        for (var index = 0; index < definition.OutputArtifactKinds.Count; index++)
        {
            var outputName = BuildOutputParameterName(
                definition.OutputArtifactKinds[index], index, definition.OutputArtifactKinds);
            metadata[$"{GeoprocessingProtocolMetadataKeys.OutputNamePrefix}{index}"] = outputName;
            metadata[$"{GeoprocessingProtocolMetadataKeys.GPServerOutputNamePrefix}{index}"] = outputName;
        }

        return metadata;
    }

    public static Dictionary<string, string> TranslateInbound(
        IReadOnlyDictionary<string, string> parameters,
        ProcessDefinition? definition = null)
    {
        var result = new Dictionary<string, string>(parameters.Count, StringComparer.OrdinalIgnoreCase);
        var specs = definition?.Parameters.ToDictionary(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ProcessParameterSpec>(0, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in parameters)
        {
            specs.TryGetValue(key, out var spec);
            result[key] = NormalizeChoice(spec, NormalizeValue(value, spec));
        }

        return result;
    }

    internal static string NormalizeValue(string value, ProcessParameterSpec? spec = null)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (spec is { ValueType: ProcessParameterValueType.WkbArray }
            && TryNormalizeMultiValue(value, out var multiValue))
        {
            return multiValue;
        }

        if (value[0] != '{')
        {
            return value;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return value;
            }

            if (root.TryGetProperty("url", out var url)
                && url.ValueKind == JsonValueKind.String
                && !root.TryGetProperty("features", out _)
                && !root.TryGetProperty("fields", out _))
            {
                return url.GetString() ?? value;
            }

            if (root.TryGetProperty("distance", out var distance)
                && root.TryGetProperty("units", out var units)
                && distance.ValueKind == JsonValueKind.Number
                && units.ValueKind == JsonValueKind.String)
            {
                return FormattableString.Invariant($"{distance.GetDouble()} {units.GetString()}");
            }
        }
        catch (JsonException)
        {
            return value;
        }

        return value;
    }

    public static string ToEsriDataType(ArtifactKind kind) => kind switch
    {
        ArtifactKind.FeatureLayer => "GPFeatureRecordSetLayer",
        ArtifactKind.Table => "GPRecordSet",
        ArtifactKind.Raster => "GPRasterDataLayer",
        ArtifactKind.File or ArtifactKind.Report or ArtifactKind.Map or ArtifactKind.AppBundle => "GPDataFile",
        ArtifactKind.Scalar => "GPString",
        _ => "GPString"
    };

    public static string ToEsriDataType(ProcessParameterValueType valueType) => valueType switch
    {
        ProcessParameterValueType.Text => "GPString",
        ProcessParameterValueType.WholeNumber => "GPLong",
        ProcessParameterValueType.FloatingPoint => "GPDouble",
        ProcessParameterValueType.Flag => "GPBoolean",
        ProcessParameterValueType.Wkb => "GPDataFile",
        ProcessParameterValueType.WkbArray => "GPMultiValue:GPDataFile",
        ProcessParameterValueType.Srid => "GPLong",
        ProcessParameterValueType.LayerId => "GPString",
        _ => "GPString"
    };

    public static string BuildOutputParameterName(
        ArtifactKind kind,
        int index,
        IReadOnlyList<ArtifactKind> allKinds)
    {
        var baseName = kind switch
        {
            ArtifactKind.FeatureLayer => "outputFeatureLayer",
            ArtifactKind.Table => "outputTable",
            ArtifactKind.Raster => "outputRaster",
            ArtifactKind.File => "outputFile",
            ArtifactKind.Report => "outputReport",
            ArtifactKind.Map => "outputMap",
            ArtifactKind.Scalar => "outputScalar",
            ArtifactKind.AppBundle => "outputBundle",
            _ => "output"
        };
        if (allKinds.Count(candidate => candidate == kind) <= 1)
        {
            return baseName;
        }

        var ordinal = allKinds.Take(index + 1).Count(candidate => candidate == kind);
        return $"{baseName}{ordinal}";
    }

    private static EsriGpTaskSummary ToSummary(string taskName, ProcessDefinition definition, bool isAlias)
        => new(
            taskName,
            definition.ProcessId,
            definition.Title,
            definition.Category,
            isAlias,
            GeoprocessingSynchronousExecutionPolicy.IsSynchronous(definition));

    private static string NormalizeChoice(ProcessParameterSpec? spec, string value)
    {
        if (spec?.AllowedValues is not { Count: > 0 } allowed || string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (allowed.Any(candidate => string.Equals(candidate, value, StringComparison.Ordinal)))
        {
            return value;
        }

        var matches = allowed
            .Where(candidate => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (matches.Count == 1)
        {
            return matches[0];
        }

        throw new GeoprocessingValidationException(
            matches.Count > 1
                ? $"Parameter '{spec.Name}': '{value}' matches more than one allowed value ignoring case; "
                    + $"supply the exact spelling from [{string.Join(", ", allowed)}]."
                : $"Parameter '{spec.Name}': '{value}' is not in the allowed values [{string.Join(", ", allowed)}].");
    }

    private static bool TryNormalizeMultiValue(string value, out string normalized)
    {
        normalized = value;
        var trimmed = value.AsSpan().Trim();
        if (trimmed.IsEmpty)
        {
            return false;
        }

        if (trimmed[0] == '[')
        {
            try
            {
                using var document = JsonDocument.Parse(value);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return false;
                }

                var items = new List<string>(document.RootElement.GetArrayLength());
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(element.GetString()))
                    {
                        return false;
                    }
                    items.Add(element.GetString()!);
                }
                normalized = EncodeStringArray(items);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        if (!value.Contains(',', StringComparison.Ordinal))
        {
            return false;
        }

        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }
        normalized = EncodeStringArray(parts);
        return true;
    }

    private static string EncodeStringArray(IReadOnlyList<string> items)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var item in items)
            {
                writer.WriteStringValue(item);
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

internal sealed record EsriGpTaskSummary(
    string TaskName,
    string ProcessId,
    string DisplayName,
    string Category,
    bool IsAlias,
    bool SupportsSynchronousExecution);

internal sealed record EsriGpTaskDescription(
    string TaskName,
    string ProcessId,
    string DisplayName,
    string Description,
    string Category,
    string ExecutionType,
    bool SupportsSynchronousExecution,
    IReadOnlyList<EsriGpTaskParameter> Parameters);

internal sealed record EsriGpTaskParameter(
    string Name,
    string DisplayName,
    string Description,
    string DataType,
    string Direction,
    string ParameterType,
    string? DefaultValue,
    IReadOnlyList<string>? AllowedValues);
