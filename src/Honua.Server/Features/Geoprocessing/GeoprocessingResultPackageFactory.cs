// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;

namespace Honua.Server.Features.Geoprocessing;

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
            var outputName = job.Spec.Parameters.GetValueOrDefault(
                    $"{GeoprocessingProtocolMetadataKeys.OutputNamePrefix}{index}")
                ?? job.Spec.Parameters.GetValueOrDefault(
                    $"{GeoprocessingProtocolMetadataKeys.GPServerOutputNamePrefix}{index}");

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
