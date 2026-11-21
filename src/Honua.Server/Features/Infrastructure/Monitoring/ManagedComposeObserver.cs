// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Admin.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Monitoring;

// The observer reads the stack through the product executor, never through local manager health.
internal interface IManagedComposeObserver
{
    Task<IReadOnlyList<OpsManagedTargetHealthView>> ReadAsync(CancellationToken cancellationToken);
}

// The executor the observer reads, with the options naming its bound target and serving service.
internal sealed record ManagedComposeExecutor(IComposeExecutorClient Client, ComposeExecutorOptions Options);

internal sealed partial class ManagedComposeObserver(
    ManagedComposeExecutor executor,
    IOptionsMonitor<ControlPlaneOptions> controlPlaneOptions,
    TimeProvider clock,
    ILogger<ManagedComposeObserver>? logger = null) : IManagedComposeObserver
{
    internal const string SourceId = "honua_ops.managed_compose";
    private static readonly TimeSpan RepeatedFailureLogInterval = TimeSpan.FromMinutes(5);
    private readonly ILogger _logger = logger ?? NullLogger<ManagedComposeObserver>.Instance;
    private readonly object _collectionGate = new();
    private Task<IReadOnlyList<OpsManagedTargetHealthView>>? _inFlight;
    // ReadAsync coalesces callers, so these histories have a single collection writer.
    private readonly Dictionary<(string TargetId, string Backend), (string FailureClass, DateTimeOffset LoggedAt)> _lastLoggedFailure = [];
    private readonly Dictionary<(string TargetId, string Backend),
        (DateTimeOffset? Collection, DateTimeOffset? Release)> _lastSuccessfulObservations = [];

    public async Task<IReadOnlyList<OpsManagedTargetHealthView>> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<IReadOnlyList<OpsManagedTargetHealthView>> collection;
        lock (_collectionGate)
        {
            // Concurrent callers share an active collection. Completed collections
            // are refreshed on the next request.
            if (_inFlight is null || _inFlight.IsCompleted)
                _inFlight = ReadCoreAsync();
            collection = _inFlight;
        }
        return await collection.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<OpsManagedTargetHealthView>> ReadCoreAsync()
    {
        var targets = controlPlaneOptions.CurrentValue.DeployTargets
            .Where(target => target.TargetKind == DeployTargetKind.LocalCompose).ToArray();
        var currentKeys = targets.Select(target => (target.TargetId, target.Backend)).ToHashSet();
        foreach (var removed in _lastSuccessfulObservations.Keys.Where(key => !currentKeys.Contains(key)).ToArray())
        {
            _lastSuccessfulObservations.Remove(removed);
            _lastLoggedFailure.Remove(removed);
        }
        var observations = new List<OpsManagedTargetHealthView>();
        foreach (var target in targets)
        {
            var bound = target.TargetId == executor.Options.TargetId && target.Backend == ComposeDeployBackend.AdapterBackendName;
            var (response, failureClass) = bound ? await ObserveAsync().ConfigureAwait(false) : (null, null);
            var available = response is { Ok: true, Services.Count: > 0 };
            var observation = new OpsManagedTargetHealthView
            {
                TargetId = target.TargetId,
                Available = available,
                ObservedAt = clock.GetUtcNow(),
                Status = !available ? "Unknown" : response!.Services.Any(row => row.Status == "Unhealthy") ? "Unhealthy"
                    : response.Services.All(row => row.Status == "Healthy") ? "Healthy" : "Unknown",
                RunningImage = available ? response!.RunningImage : null,
                ImageId = available ? response!.ImageId : null,
                ServerVersion = available ? response!.ServerVersion : null,
                ServingIncarnation = available ? response!.Services.FirstOrDefault(row => row.Service == executor.Options.ServiceName)?.Incarnation : null,
                ServiceIncarnations = available ? response!.Services.Where(row => row.Incarnation is not null)
                    .ToDictionary(row => row.Service, row => row.Incarnation!, StringComparer.Ordinal) : new Dictionary<string, string>(),
                Services = available ? response!.Services.Select(row => new OpsHealthCheckEntryView
                {
                    Name = row.Service,
                    Status = row.Status,
                    Description = row.Description,
                    DurationMs = 0
                }).ToArray() : []
            };
            if (bound)
                RecordReadback((target.TargetId, target.Backend), failureClass, observation.ObservedAt);
            AttachSuccessfulObservation((target.TargetId, target.Backend), observation);
            observations.Add(observation);
        }
        return observations;
    }

    // Unavailable readback is projected by the caller; the failure class is returned for the operator log.
    private async Task<(ComposeExecutorResponse? Response, string? FailureClass)> ObserveAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            var response = await executor.Client.ExecuteAsync(new() { Op = "observe" }, timeout.Token).ConfigureAwait(false);
            return (response, response switch
            {
                { Ok: true, Services.Count: > 0 } => null,
                { Ok: true } => "executor reported no services",
                _ => "executor code " + (response.Code ?? "none")
            });
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return (null, OpsFindingsReadFailure.DescribeFailure(ex));
        }
    }

    // Exception text and the executor message can carry host detail, so only the failure class is
    // logged. Every Ops read collects, so an unchanged failure is logged at most every five minutes.
    private void RecordReadback((string TargetId, string Backend) key, string? failureClass, DateTimeOffset now)
    {
        if (failureClass is null)
        {
            _lastLoggedFailure.Remove(key);
            return;
        }
        if (_lastLoggedFailure.TryGetValue(key, out var last)
            && last.FailureClass == failureClass && now - last.LoggedAt < RepeatedFailureLogInterval)
        {
            return;
        }
        _lastLoggedFailure[key] = (failureClass, now);
        ReadbackFailed(_logger, key.TargetId, failureClass);
    }

    [LoggerMessage(EventId = 7464, Level = LogLevel.Warning,
        Message = "Managed Compose target {TargetId} health readback failed: {FailureClass}.")]
    private static partial void ReadbackFailed(ILogger logger, string targetId, string failureClass);

    private void AttachSuccessfulObservation((string TargetId, string Backend) key, OpsManagedTargetHealthView target)
    {
        var previous = _lastSuccessfulObservations.GetValueOrDefault(key);
        var collection = IsCompleteCollection(target) ? target.ObservedAt : previous.Collection;
        var release = HasObservedRelease(target) ? target.ObservedAt : previous.Release;
        _lastSuccessfulObservations[key] = (collection, release);
        target.LastSuccessfulCollectionAt = collection;
        target.LastSuccessfulReleaseAt = release;
    }

    private static bool IsCompleteCollection(OpsManagedTargetHealthView target)
        => target.Available && target.Status != "Unknown"
           && target.Services.All(service => service.Status is "Healthy" or "Unhealthy");

    private static bool HasObservedRelease(OpsManagedTargetHealthView target)
        => target is { Available: true, RunningImage: not null, ServerVersion: not null };

    private static DateTimeOffset? LastCommonSuccess(IEnumerable<DateTimeOffset?> successTimes)
    {
        var times = successTimes.ToArray();
        return times.Length > 0 && times.All(time => time.HasValue) ? times.Min() : null;
    }

    internal static EvidenceSourceEnvelope Source(IReadOnlyList<OpsManagedTargetHealthView> targets)
    {
        if (targets.Count == 0)
        {
            return EvidencePostureFactory.NotConfigured(SourceId, EvidencePostureVocabulary.BackendKinds.Composite, "compose-executor");
        }
        if (targets.All(IsCompleteCollection))
        {
            return EvidencePostureFactory.Complete(SourceId, EvidencePostureVocabulary.BackendKinds.Composite, "compose-executor",
                targets.Min(target => target.ObservedAt), TimeSpan.FromMinutes(2));
        }

        var successful = LastCommonSuccess(targets.Select(target => target.LastSuccessfulCollectionAt));
        var reason = targets.All(target => target.Available)
            ? EvidencePostureVocabulary.ReasonCodes.ReadbackIncomplete
            : EvidencePostureVocabulary.ReasonCodes.SourceUnavailable;
        return WithReadbackDeadline(EvidencePostureFactory.Unavailable(SourceId, EvidencePostureVocabulary.BackendKinds.Composite, "compose-executor",
            reason, observedAt: successful, lastSuccessfulAt: successful, maximumAge: TimeSpan.FromMinutes(2)));
    }

    internal static DeployPreflightPlatformRelease ProjectRelease(
        ControlPlaneOptions options, IReadOnlyList<OpsManagedTargetHealthView> targets,
        string sourceId = EvidencePostureVocabulary.SourceIds.PlatformReleaseStatus,
        DateTimeOffset? evaluatedAt = null)
    {
        var now = evaluatedAt ?? TimeProvider.System.GetUtcNow();
        var skew = PlatformReleaseSkewProjector.Build(options);
        var serving = skew.Serving.Select(projection =>
        {
            var live = targets.SingleOrDefault(target => target.TargetId == projection.Id);
            var known = live is not null && HasObservedRelease(live);
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
        var applicableIds = options.DeployTargets.Where(target => target.TargetKind == DeployTargetKind.LocalCompose)
            .Select(target => target.TargetId).ToHashSet(StringComparer.Ordinal);
        var applicable = serving.Where(target => applicableIds.Contains(target.Id)).ToArray();
        var observed = applicable.Where(target => target.ObservationStatus == "observed").ToArray();
        var coverage = new EvidenceSourceCoverage
        {
            ExpectedComponentIds = applicable.Select(target => target.Id).ToArray(),
            IncludedComponentIds = observed.Select(target => target.Id).ToArray()
        };
        var successful = LastCommonSuccess(applicable.Select(projection =>
            targets.SingleOrDefault(target => target.TargetId == projection.Id)?.LastSuccessfulReleaseAt));
        var failureReason = ReleaseReadbackFailureReason(applicable, targets);
        var source = applicable.Length == 0
            ? EvidencePostureFactory.Complete(sourceId, EvidencePostureVocabulary.BackendKinds.ConfigProjection,
                "control-plane-options", now, TimeSpan.FromMinutes(5), coverage)
            : observed.Length == applicable.Length
                ? EvidencePostureFactory.Complete(sourceId, EvidencePostureVocabulary.BackendKinds.Composite,
                    "compose-executor", observed.Min(target => target.ObservedAt!.Value), TimeSpan.FromMinutes(2), coverage)
                : observed.Length > 0
                    ? EvidencePostureFactory.Partial(sourceId, EvidencePostureVocabulary.BackendKinds.Composite,
                        "compose-executor", observed.Min(target => target.ObservedAt!.Value), TimeSpan.FromMinutes(2),
                        EvidencePostureVocabulary.ReasonCodes.IncompleteCoverage, coverage)
                    : WithReadbackDeadline(EvidencePostureFactory.Unavailable(sourceId, EvidencePostureVocabulary.BackendKinds.Composite,
                        "compose-executor", failureReason, observedAt: successful, lastSuccessfulAt: successful,
                        maximumAge: TimeSpan.FromMinutes(2), coverage: coverage));
        return new()
        {
            EvidencePosture = EvidencePostureFactory.Build(now, source),
            ReleaseVersion = skew.ReleaseVersion,
            ReleaseDeclared = skew.ReleaseDeclared,
            IsCoVersioned = skew.IsCoVersioned && applicable.All(target => target.ObservationStatus == "observed" && !target.Skewed),
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

    private static EvidenceSourceEnvelope WithReadbackDeadline(EvidenceSourceEnvelope readback)
        => new()
        {
            SourceId = readback.SourceId,
            BackendKind = readback.BackendKind,
            BackendId = readback.BackendId,
            ObservedAt = readback.ObservedAt,
            LastSuccessfulAt = readback.LastSuccessfulAt,
            Completeness = readback.Completeness,
            ReasonCodes = readback.ReasonCodes,
            Coverage = readback.Coverage,
            MaximumAgeSeconds = readback.MaximumAgeSeconds,
            ValidUntil = readback.ObservedAt is { } observed && readback.MaximumAgeSeconds is { } maximumAge
                ? observed.AddSeconds(maximumAge)
                : null
        };

    private static string ReleaseReadbackFailureReason(DeployPreflightPlaneProjection[] applicable,
        IReadOnlyList<OpsManagedTargetHealthView> targets)
    {
        var unobserved = applicable.Where(projection => projection.ObservationStatus != "observed")
            .Select(projection => targets.SingleOrDefault(target => target.TargetId == projection.Id)).ToArray();
        if (unobserved.Any(target => target is not { Available: true }))
        {
            return EvidencePostureVocabulary.ReasonCodes.SourceUnavailable;
        }
        return unobserved.Any(target => target is { Status: "Unhealthy" })
            ? EvidencePostureVocabulary.ReasonCodes.TargetUnready
            : EvidencePostureVocabulary.ReasonCodes.ReadbackIncomplete;
    }
}
