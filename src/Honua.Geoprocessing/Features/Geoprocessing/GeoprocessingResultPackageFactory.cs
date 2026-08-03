// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.ControlPlane;

namespace Honua.Geoprocessing;

/// <summary>
/// Builds durable <see cref="AnalysisResultPackage"/> envelopes from terminal execution jobs.
/// </summary>
internal static class GeoprocessingResultPackageFactory
{
    public static AnalysisResultPackage Create(ExecutionJobRecord job, IProcessCatalog processCatalog)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(processCatalog);

        if (!IsTerminal(job.Status))
        {
            throw new InvalidOperationException(
                $"Execution job '{job.OperationId}' is not terminal and cannot produce a result package.");
        }

        var artifacts = job.Status == ExecutionJobStatus.Succeeded
            ? BuildArtifacts(job, processCatalog)
            : [];
        var provenance = BuildProvenance(job, processCatalog, artifacts);

        return job.Status switch
        {
            ExecutionJobStatus.Succeeded => AnalysisResultPackage.CreateCompleted(
                CreateResultPackageId(job),
                new ResultSummary
                {
                    Title = $"Results for {ResolvePlanLabel(job)}",
                    Description = artifacts.Length == 1
                        ? "Produced 1 artifact."
                        : $"Produced {artifacts.Length} artifacts."
                },
                artifacts,
                [],
                provenance),
            ExecutionJobStatus.Failed => AnalysisResultPackage.CreateFailed(
                CreateResultPackageId(job),
                new ResultSummary
                {
                    Title = $"Job {ResolvePlanLabel(job)} failed",
                    Description = job.ErrorMessage ?? "The geoprocessing job failed."
                },
                [new GeoprocessingError
                {
                    Kind = GeoprocessingErrorKind.ExecutionFailed,
                    Message = job.ErrorMessage ?? "The geoprocessing job failed."
                }],
                provenance),
            ExecutionJobStatus.Cancelled => new AnalysisResultPackage
            {
                ResultPackageId = CreateResultPackageId(job),
                Status = GeoprocessingWorkflowStatus.Cancelled,
                Summary = new ResultSummary
                {
                    Title = $"Job {ResolvePlanLabel(job)} cancelled",
                    Description = job.ErrorMessage ?? "The geoprocessing job was cancelled."
                },
                Provenance = provenance,
                Errors =
                [
                    new GeoprocessingError
                    {
                        Kind = GeoprocessingErrorKind.Cancelled,
                        Message = job.ErrorMessage ?? "The geoprocessing job was cancelled."
                    }
                ]
            },
            _ => throw new InvalidOperationException(
                $"Execution job '{job.OperationId}' status '{job.Status}' is not supported for result packages.")
        };
    }

    public static string CreateResultPackageId(ExecutionJobRecord job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return $"{job.OperationId}:v{job.Version}";
    }

    private static ArtifactRef[] BuildArtifacts(
        ExecutionJobRecord job,
        IProcessCatalog processCatalog)
    {
        if (job.ArtifactReferences.Count == 0)
        {
            return [];
        }

        var outputKinds = ResolveOutputArtifactKinds(job, processCatalog);
        var artifacts = new ArtifactRef[job.ArtifactReferences.Count];
        for (var index = 0; index < job.ArtifactReferences.Count; index++)
        {
            var reference = job.ArtifactReferences[index];
            var kind = index < outputKinds.Length ? outputKinds[index] : ArtifactKind.File;

            if (RasterOutputArtifactReference.TryParseOutput(reference, out var rasterOutput)
                && rasterOutput is not null)
            {
                var rasterSlot = index < outputKinds.Length && outputKinds[index] == ArtifactKind.Raster
                    ? index
                    : Array.IndexOf(outputKinds, ArtifactKind.Raster);
                var advertisedRasterOutputName = rasterSlot < 0
                    ? null
                    : job.Spec.Parameters.GetValueOrDefault(
                            $"{GeoprocessingProtocolMetadataKeys.OutputNamePrefix}{rasterSlot}")
                        ?? job.Spec.Parameters.GetValueOrDefault(
                            $"{GeoprocessingProtocolMetadataKeys.GPServerOutputNamePrefix}{rasterSlot}");
                artifacts[index] = BuildRasterArtifact(rasterOutput, advertisedRasterOutputName);
                continue;
            }

            // Positional slot assignment assumes one published artifact per declared
            // output. A process that advertises ALTERNATIVE output shapes — e.g.
            // imagery.classify declares [Raster, FeatureLayer] because the backend
            // decides whether a scene yields a classification raster or detected
            // features — publishes fewer artifacts than it declares, so index 0
            // would label a GeoJSON result as the Raster/outputRaster slot and leave
            // the advertised feature slot unreachable. When that mismatch exists,
            // resolve the slot from the artifact's OWN media type instead, and use
            // the matching declared position for the output-name lookup so the
            // protocol adapters surface the name they advertised.
            var slotIndex = index;
            if (Execution.OutputSlotResolver.TryResolveAlternativeSlot(
                    reference, index, outputKinds, out var alternativeSlot, out var alternativeKind))
            {
                kind = alternativeKind;
                slotIndex = alternativeSlot;
            }

            var outputName = job.Spec.Parameters.GetValueOrDefault(
                    $"{GeoprocessingProtocolMetadataKeys.OutputNamePrefix}{slotIndex}")
                ?? job.Spec.Parameters.GetValueOrDefault(
                    $"{GeoprocessingProtocolMetadataKeys.GPServerOutputNamePrefix}{slotIndex}");

            Dictionary<string, string>? metadata = null;
            if (!string.IsNullOrWhiteSpace(outputName))
            {
                metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [GeoprocessingProtocolMetadataKeys.GeoServicesOutputParameterMetadataKey] = outputName
                };
            }

            artifacts[index] = new ArtifactRef
            {
                ArtifactId = $"{job.OperationId}:artifact:{index + 1}",
                Kind = kind,
                Label = string.IsNullOrWhiteSpace(outputName)
                    ? BuildArtifactLabel(kind, index)
                    : outputName,
                Uri = string.IsNullOrWhiteSpace(reference) ? null : reference,
                ContentType = InferContentType(reference, kind),
                Metadata = metadata ?? new Dictionary<string, string>()
            };
        }

        return artifacts;
    }

    private static ArtifactRef BuildRasterArtifact(
        RasterOutputDescriptor output,
        string? advertisedOutputName)
    {
        var checksum = output.Content.Checksum!;
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["rasterOutputDescriptor"] = RasterOutputJson.Serialize(output),
            ["sizeBytes"] = output.Content.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["checksum"] = checksum.Algorithm + ":" + checksum.Value,
            ["mediaType"] = output.Content.MediaType,
            ["crs"] = output.Grid.Crs,
            ["width"] = output.Grid.Width.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["height"] = output.Grid.Height.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["bandCount"] = output.Grid.BandCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["geoTransform"] = string.Join(",", output.Grid.GeoTransform.Select(value =>
                value.ToString("R", System.Globalization.CultureInfo.InvariantCulture))),
            ["engine"] = output.Engine.Name,
            ["engineVersion"] = output.Engine.Version,
            ["jobId"] = output.Lineage.JobId,
            ["attempt"] = output.Lineage.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["processId"] = output.Lineage.ProcessId,
            ["sourceArtifactIds"] = string.Join(",", output.Lineage.SourceArtifactIds)
        };
        if (!string.IsNullOrWhiteSpace(advertisedOutputName))
        {
            metadata[GeoprocessingProtocolMetadataKeys.GeoServicesOutputParameterMetadataKey] =
                advertisedOutputName;
        }

        var stableUri = output switch
        {
            ObjectStoreRasterOutputDescriptor
            {
                Encoding: RasterOutputEncoding.CloudOptimizedGeoTiff
            } => "/api/v1/geoprocessing/raster-outputs/" + output.ArtifactId,
            PostgisRasterOutputDescriptor => "urn:honua:postgis-raster:" + output.ArtifactId,
            _ => "urn:honua:raster:" + output.ArtifactId
        };

        return new ArtifactRef
        {
            ArtifactId = output.ArtifactId,
            Kind = ArtifactKind.Raster,
            Label = string.IsNullOrWhiteSpace(advertisedOutputName)
                ? output.OutputName
                : advertisedOutputName,
            Uri = stableUri,
            ContentType = output.Content.MediaType,
            Metadata = metadata
        };
    }

    private static ProvenanceRecord BuildProvenance(
        ExecutionJobRecord job,
        IProcessCatalog processCatalog,
        ArtifactRef[] artifacts)
        => new()
        {
            Sources = [],
            ProcessDefinitions = ResolveProcessDefinitions(job, processCatalog),
            ExecutedAt = job.CompletedAt,
            GeneratedArtifactIds = artifacts.Select(artifact => artifact.ArtifactId).ToArray()
        };

    private static string[] ResolveProcessDefinitions(
        ExecutionJobRecord job,
        IProcessCatalog processCatalog)
    {
        if (job.Spec.Parameters.TryGetValue(
                ExecutionJobParameterKeys.GeoprocessingProcessDefinitions,
                out var serializedProcessIds))
        {
            return SplitMetadataList(serializedProcessIds);
        }

        if (job.Spec.Parameters.TryGetValue(
                GeoprocessingProtocolMetadataKeys.GPServerTaskName,
                out var gpTaskName) &&
            !string.IsNullOrWhiteSpace(gpTaskName) &&
            processCatalog.GetProcess(gpTaskName) != null)
        {
            return [gpTaskName];
        }

        return [];
    }

    private static ArtifactKind[] ResolveOutputArtifactKinds(
        ExecutionJobRecord job,
        IProcessCatalog processCatalog)
    {
        if (job.Spec.Parameters.TryGetValue(
                ExecutionJobParameterKeys.GeoprocessingOutputArtifactKinds,
                out var serializedKinds))
        {
            var parsedKinds = SplitMetadataList(serializedKinds)
                .Select(raw => Enum.TryParse<ArtifactKind>(raw, ignoreCase: true, out var parsed)
                    ? (ArtifactKind?)parsed
                    : null)
                .Where(kind => kind.HasValue)
                .Select(kind => kind!.Value)
                .ToArray();

            if (parsedKinds.Length > 0)
            {
                return parsedKinds;
            }
        }

        var processDefinitions = ResolveProcessDefinitions(job, processCatalog);
        if (processDefinitions.Length == 1)
        {
            var definition = processCatalog.GetProcess(processDefinitions[0]);
            if (definition != null)
            {
                return [.. definition.OutputArtifactKinds];
            }
        }

        return [];
    }

    private static string ResolvePlanLabel(ExecutionJobRecord job)
        => job.Spec.Parameters.GetValueOrDefault(ExecutionJobParameterKeys.GeoprocessingPlanId)
            ?? job.OperationId;

    private static string BuildArtifactLabel(ArtifactKind kind, int index)
        => kind switch
        {
            ArtifactKind.FeatureLayer => $"featureLayer{index + 1}",
            ArtifactKind.Table => $"table{index + 1}",
            ArtifactKind.Raster => $"raster{index + 1}",
            ArtifactKind.File => $"file{index + 1}",
            ArtifactKind.Report => $"report{index + 1}",
            ArtifactKind.Map => $"map{index + 1}",
            ArtifactKind.Scalar => $"scalar{index + 1}",
            ArtifactKind.AppBundle => $"bundle{index + 1}",
            _ => $"artifact{index + 1}"
        };

    private static string? InferContentType(string? reference, ArtifactKind kind)
    {
        if (!string.IsNullOrWhiteSpace(reference))
        {
            if (reference.EndsWith(".geojson", StringComparison.OrdinalIgnoreCase) ||
                reference.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && kind == ArtifactKind.FeatureLayer)
            {
                return "application/geo+json";
            }

            if (reference.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                return "application/json";
            }

            if (reference.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                return "text/csv";
            }

            if (reference.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) ||
                reference.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase))
            {
                return "image/tiff";
            }
        }

        return kind switch
        {
            ArtifactKind.FeatureLayer => "application/geo+json",
            ArtifactKind.Table or ArtifactKind.Report or ArtifactKind.Scalar => "application/json",
            _ => null
        };
    }

    private static string[] SplitMetadataList(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(
                ExecutionJobParameterKeys.MetadataListSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsTerminal(ExecutionJobStatus status)
        => status is ExecutionJobStatus.Succeeded
            or ExecutionJobStatus.Failed
            or ExecutionJobStatus.Cancelled;
}
