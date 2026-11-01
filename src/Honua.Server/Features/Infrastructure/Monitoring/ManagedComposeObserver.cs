// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Admin.Models;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Monitoring;

/// <summary>Reads the registered Compose stack through the product executor, never through local manager health.</summary>
internal interface IManagedComposeObserver
{
    Task<IReadOnlyList<OpsManagedTargetHealthView>> ReadAsync(CancellationToken cancellationToken);
}

internal sealed class ManagedComposeObserver(
    IComposeExecutorClient client,
    IOptions<ComposeExecutorOptions> executorOptions,
    IOptionsMonitor<ControlPlaneOptions> controlPlaneOptions,
    TimeProvider clock) : IManagedComposeObserver
{
    internal const string SourceId = "honua_ops.managed_compose";

    public async Task<IReadOnlyList<OpsManagedTargetHealthView>> ReadAsync(CancellationToken cancellationToken)
    {
        var targets = controlPlaneOptions.CurrentValue.DeployTargets
            .Where(target => target.TargetKind == DeployTargetKind.LocalCompose).ToArray();
        var observations = new List<OpsManagedTargetHealthView>();
        foreach (var target in targets)
        {
            ComposeExecutorResponse? response = null;
            if (target.TargetId == executorOptions.Value.TargetId && target.Backend == ComposeDeployBackend.AdapterBackendName)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(25));
                try
                {
                    response = await client.ExecuteAsync(new() { Op = "observe" }, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            var available = response is { Ok: true, Services.Count: > 0 };
            observations.Add(new()
            {
                TargetId = target.TargetId,
                Available = available,
                ObservedAt = clock.GetUtcNow(),
                Status = !available ? "Unknown" : response!.Services.Any(row => row.Status == "Unhealthy") ? "Unhealthy"
                    : response.Services.All(row => row.Status == "Healthy") ? "Healthy" : "Unknown",
                RunningImage = available ? response!.RunningImage : null,
                ImageId = available ? response!.ImageId : null,
                ServerVersion = available ? response!.ServerVersion : null,
                ServiceIncarnations = available ? response!.Services.Where(row => row.Incarnation is not null)
                    .ToDictionary(row => row.Service, row => row.Incarnation!, StringComparer.Ordinal) : new Dictionary<string, string>(),
                Services = available ? response!.Services.Select(row => new OpsHealthCheckEntryView
                {
                    Name = row.Service,
                    Status = row.Status,
                    Description = row.Description,
                    DurationMs = 0
                }).ToArray() : []
            });
        }
        return observations;
    }

    internal static EvidenceSourceEnvelope Source(IReadOnlyList<OpsManagedTargetHealthView> targets)
        => targets.Count == 0
            ? EvidencePostureFactory.NotConfigured(SourceId, EvidencePostureVocabulary.BackendKinds.Composite, "compose-executor")
            : targets.All(target => target.Available && target.Status != "Unknown")
                ? EvidencePostureFactory.Complete(SourceId, EvidencePostureVocabulary.BackendKinds.Composite, "compose-executor",
                    targets.Min(target => target.ObservedAt), TimeSpan.FromMinutes(2))
                : EvidencePostureFactory.Unavailable(SourceId, EvidencePostureVocabulary.BackendKinds.Composite, "compose-executor",
                    EvidencePostureVocabulary.ReasonCodes.SourceUnavailable, maximumAge: TimeSpan.FromMinutes(2));

    internal static DeployPreflightPlatformRelease ProjectRelease(
        ControlPlaneOptions options, IReadOnlyList<OpsManagedTargetHealthView> targets,
        string sourceId = EvidencePostureVocabulary.SourceIds.PlatformReleaseStatus)
    {
        var skew = PlatformReleaseSkewProjector.Build(options);
        var serving = skew.Serving.Select(projection =>
        {
            var live = targets.SingleOrDefault(target => target.TargetId == projection.Id);
            var known = live is { Available: true, RunningImage: not null, ServerVersion: not null };
            return new DeployPreflightPlaneProjection
            {
                Id = projection.Id,
                RuntimeProfile = projection.RuntimeProfile,
                EffectiveArtifactReference = projection.EffectiveArtifactReference,
                ProjectedFromRelease = projection.ProjectedFromRelease,
                ObservedArtifactReference = live?.RunningImage,
                ObservedImageId = live?.ImageId,
                ServerVersion = live?.ServerVersion,
                ObservedAt = live?.Available == true ? live.ObservedAt : null,
                ObservationStatus = live is null ? "notObserved" : known ? "observed" : "unavailable",
                Skewed = projection.Skewed || (live?.RunningImage is not null && live.RunningImage != options.PlatformRelease.ServingArtifactReference)
            };
        }).ToArray();
        var observed = serving.Where(target => target.ObservationStatus == "observed").ToArray();
        var coverage = new EvidenceSourceCoverage
        {
            ExpectedComponentIds = serving.Select(target => target.Id).ToArray(),
            IncludedComponentIds = observed.Select(target => target.Id).ToArray()
        };
        var source = serving.Length == 0
            ? EvidencePostureFactory.Complete(sourceId, EvidencePostureVocabulary.BackendKinds.ConfigProjection,
                "control-plane-options", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), coverage)
            : observed.Length == serving.Length
                ? EvidencePostureFactory.Complete(sourceId, EvidencePostureVocabulary.BackendKinds.Composite,
                    "compose-executor", observed.Min(target => target.ObservedAt!.Value), TimeSpan.FromMinutes(2), coverage)
                : observed.Length > 0
                    ? EvidencePostureFactory.Partial(sourceId, EvidencePostureVocabulary.BackendKinds.Composite,
                        "compose-executor", observed.Min(target => target.ObservedAt!.Value), TimeSpan.FromMinutes(2),
                        EvidencePostureVocabulary.ReasonCodes.IncompleteCoverage, coverage)
                    : EvidencePostureFactory.Unavailable(sourceId, EvidencePostureVocabulary.BackendKinds.Composite,
                        "compose-executor", EvidencePostureVocabulary.ReasonCodes.SourceUnavailable,
                        maximumAge: TimeSpan.FromMinutes(2), coverage: coverage);
        return new()
        {
            EvidencePosture = EvidencePostureFactory.Build(DateTimeOffset.UtcNow, source),
            ReleaseVersion = skew.ReleaseVersion,
            ReleaseDeclared = skew.ReleaseDeclared,
            IsCoVersioned = skew.IsCoVersioned && serving.All(target => target.ObservationStatus == "observed" && !target.Skewed),
            Serving = serving,
            Execution = skew.Execution.Select(projection => new DeployPreflightPlaneProjection
            {
                Id = projection.Id,
                RuntimeProfile = projection.RuntimeProfile,
                EffectiveArtifactReference = projection.EffectiveArtifactReference,
                ProjectedFromRelease = projection.ProjectedFromRelease,
                Skewed = projection.Skewed
            }).ToArray(),
            SkewedIds = serving.Where(target => target.Skewed).Select(target => target.Id)
                .Concat(skew.Execution.Where(target => target.Skewed).Select(target => target.Id)).ToArray()
        };
    }
}
