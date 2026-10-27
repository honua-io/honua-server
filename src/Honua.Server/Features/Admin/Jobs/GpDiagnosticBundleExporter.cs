// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Geoprocessing.Diagnostics;

namespace Honua.Server.Features.Admin.Jobs;

/// <summary>
/// Attaches versioned GP evidence to a diagnostic-bundle v1 envelope using the job
/// diagnostics projection (honua-server#6219). This does not execute a job, read input
/// bytes from the job store, or change the admin diagnostics response. Bytes are included
/// only when the caller passes them and the export request authorizes that export.
/// </summary>
internal static class GpDiagnosticBundleExporter
{
    public static GpDiagnosticBundleDocument Export(
        string bundleJson,
        ConsoleJobDiagnostics projection,
        GpDiagnosticInputExportRequest? export = null,
        IReadOnlyList<GpDiagnosticInputDraft>? inputs = null)
    {
        ArgumentNullException.ThrowIfNull(bundleJson);
        ArgumentNullException.ThrowIfNull(projection);
        return GpDiagnosticBundleDocument.Parse(bundleJson).WithEvidence(ToDraft(projection, inputs), export);
    }

    private static GpDiagnosticEvidenceDraft ToDraft(
        ConsoleJobDiagnostics projection,
        IReadOnlyList<GpDiagnosticInputDraft>? inputs)
    {
        var release = projection.Release;
        var dependencies = new List<GpDiagnosticDependencyDraft>();
        if (!string.IsNullOrWhiteSpace(release.DependencyManifest))
        {
            dependencies.Add(new GpDiagnosticDependencyDraft
            {
                Name = "manifest",
                Version = release.DependencyManifest
            });
        }

        foreach (var definition in release.ProcessDefinitions)
        {
            if (!string.IsNullOrWhiteSpace(definition))
            {
                dependencies.Add(new GpDiagnosticDependencyDraft { Name = definition });
            }
        }

        var events = new List<GpDiagnosticEventDraft>(projection.Events.Items.Length);
        foreach (var entry in projection.Events.Items)
        {
            var attributes = new List<GpDiagnosticAttributeDraft>();
            if (entry.Metadata is not null)
            {
                foreach (var pair in entry.Metadata)
                {
                    attributes.Add(new GpDiagnosticAttributeDraft { Name = pair.Key, Value = pair.Value });
                }
            }

            events.Add(new GpDiagnosticEventDraft
            {
                Timestamp = entry.Timestamp,
                Level = entry.Level,
                Phase = entry.Phase,
                Message = entry.Message,
                Attributes = attributes
            });
        }

        return new GpDiagnosticEvidenceDraft
        {
            JobId = projection.JobId,
            CorrelationId = projection.CorrelationId,
            TraceId = projection.TraceId,
            ReleaseState = release.State,
            SourceRepository = release.SourceRepository,
            SourceRef = release.SourceRef,
            WorkflowVersion = release.WorkflowVersion,
            ProcessRelease = release.ProcessRelease ?? release.ProcessVersion,
            LockDigest = release.LockDigest,
            Missing = release.Missing,
            ServerVersion = release.ServerVersion,
            Runtime = release.Runtime,
            WorkerImage = release.WorkerImage,
            Entrypoint = release.Entrypoint,
            DependencyLockDigest = release.DependencyLockDigest,
            Dependencies = dependencies,
            ErrorMessage = projection.Failure?.Message,
            ErrorClassification = projection.Failure?.Classification,
            ErrorPhase = projection.Failure?.Phase,
            StepOrdinal = projection.Failure?.StepOrdinal,
            ErrorCode = projection.Failure?.ErrorCode,
            NativeExitCode = projection.Failure?.NativeExitCode,
            StderrExcerpt = projection.Failure?.StderrExcerpt,
            EventsTruncated = projection.Events.Truncated,
            Events = events,
            ArtifactsState = projection.Artifacts.State,
            Artifacts = projection.Artifacts.Items.Select(artifact => new GpDiagnosticArtifactDraft
            {
                Id = artifact.ArtifactId,
                Kind = artifact.Kind,
                Availability = artifact.Availability,
                Label = artifact.Label,
                ByteSize = artifact.SizeBytes
            }).ToArray(),
            Inputs = inputs?.ToArray() ?? ResourceInputs(projection),
            Notes = projection.Evidence.Missing
                .Concat(projection.Evidence.Truncated)
                .Concat(projection.Evidence.Notes)
                .ToArray()
        };
    }

    private static GpDiagnosticInputDraft[] ResourceInputs(ConsoleJobDiagnostics projection)
    {
        var inputs = new List<GpDiagnosticInputDraft>();
        var index = 0;
        foreach (var reference in projection.Identity.ResourceRefs)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                continue;
            }

            inputs.Add(new GpDiagnosticInputDraft
            {
                Id = string.Create(CultureInfo.InvariantCulture, $"resource-{index}"),
                Role = "resource-ref",
                Reference = reference
            });
            index++;
        }

        return inputs.ToArray();
    }
}
