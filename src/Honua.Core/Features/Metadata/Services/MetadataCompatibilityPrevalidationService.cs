// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Metadata.Services;

/// <summary>
/// Default service for Metadata v2 compatibility prevalidation.
/// </summary>
public sealed class MetadataCompatibilityPrevalidationService(
    IMetadataV2EnvironmentSnapshotReader snapshotReader,
    IMetadataReleasePackageStore packageStore,
    TimeProvider? timeProvider,
    ILogger<MetadataCompatibilityPrevalidationService> logger) : IMetadataCompatibilityPrevalidationService
{
    private const int MaxDataScripts = 100;
    private const int MaxScriptFields = 1000;
    private static readonly ActivitySource ActivitySource = new("Honua.Core.Metadata");

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<MetadataCompatibilityReport> PrevalidateAsync(
        MetadataCompatibilityPrevalidationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var targetEnvironment = NormalizeTargetEnvironment(request.TargetEnvironment);
        var dataScripts = ValidateDataScripts(request.DataScripts);

        using var activity = ActivitySource.StartActivity(
            "honua.metadata.compatibility.prevalidate",
            ActivityKind.Internal);
        activity?.SetTag("metadata.target_environment", targetEnvironment);
        activity?.SetTag("metadata.script.count", dataScripts.Count);

        MetadataCompatibilityPrevalidationLog.Started(logger, targetEnvironment, dataScripts.Count);

        var package = await ResolvePackageAsync(request, targetEnvironment, dataScripts.Count, cancellationToken)
            .ConfigureAwait(false);
        if (package.Report is not null)
        {
            TagReport(activity, package.Report);
            return package.Report;
        }

        var resolvedPackage = package.Package!;
        activity?.SetTag("metadata.package_id", resolvedPackage.PackageId);
        activity?.SetTag("metadata.source_revision", resolvedPackage.SourceRevision);

        var sourceSnapshot = await snapshotReader.GetByRevisionAsync(
                resolvedPackage.SourceEnvironment,
                resolvedPackage.SourceRevision,
                cancellationToken)
            .ConfigureAwait(false);
        if (sourceSnapshot is null)
        {
            MetadataCompatibilityPrevalidationLog.StateUnavailable(
                logger,
                resolvedPackage.PackageId,
                resolvedPackage.SourceEnvironment,
                targetEnvironment,
                "source-revision");
            var report = MetadataCompatibilityAnalyzer.CreateUnavailableReport(
                resolvedPackage,
                targetEnvironment,
                _timeProvider.GetUtcNow(),
                resolvedPackage.SourceEnvironment,
                $"source revision {resolvedPackage.SourceRevision}",
                "unavailable",
                "The release package source graph revision is unavailable.",
                dataScripts.Count);
            TagReport(activity, report);
            return report;
        }

        var targetSnapshot = await snapshotReader.GetCurrentAsync(targetEnvironment, cancellationToken)
            .ConfigureAwait(false);
        if (targetSnapshot is null)
        {
            MetadataCompatibilityPrevalidationLog.StateUnavailable(
                logger,
                resolvedPackage.PackageId,
                resolvedPackage.SourceEnvironment,
                targetEnvironment,
                "target-current");
            var report = MetadataCompatibilityAnalyzer.CreateUnavailableReport(
                resolvedPackage,
                targetEnvironment,
                _timeProvider.GetUtcNow(),
                targetEnvironment,
                "target current Metadata v2 graph",
                "unavailable",
                "The target environment graph snapshot is unavailable.",
                dataScripts.Count);
            TagReport(activity, report);
            return report;
        }

        var compatibilityReport = MetadataCompatibilityAnalyzer.Analyze(
            resolvedPackage,
            sourceSnapshot,
            targetSnapshot,
            dataScripts,
            _timeProvider.GetUtcNow());
        TagReport(activity, compatibilityReport);

        MetadataCompatibilityPrevalidationLog.Completed(
            logger,
            resolvedPackage.PackageId,
            targetEnvironment,
            compatibilityReport.Status,
            compatibilityReport.Findings.Count,
            compatibilityReport.UncoveredErrorCount,
            compatibilityReport.CoveredErrorCount,
            compatibilityReport.AffectedDependents.Count);

        return compatibilityReport;
    }

    private async Task<PackageResolution> ResolvePackageAsync(
        MetadataCompatibilityPrevalidationRequest request,
        string targetEnvironment,
        int scriptCount,
        CancellationToken cancellationToken)
    {
        var hasId = request.ReleasePackageId is { } packageId && packageId != Guid.Empty;
        if (request.ReleasePackageId == Guid.Empty)
        {
            throw new ArgumentException("ReleasePackageId must not be empty.", nameof(request));
        }

        var hasInline = request.ReleasePackage is not null;
        if (hasId == hasInline)
        {
            throw new ArgumentException("Exactly one of ReleasePackageId or ReleasePackage is required.", nameof(request));
        }

        if (hasInline)
        {
            return new PackageResolution(request.ReleasePackage, null);
        }

        var persisted = await packageStore.GetAsync(request.ReleasePackageId!.Value, cancellationToken).ConfigureAwait(false);
        if (persisted is not null)
        {
            return new PackageResolution(persisted, null);
        }

        MetadataCompatibilityPrevalidationLog.StateUnavailable(
            logger,
            request.ReleasePackageId.Value,
            "unknown",
            targetEnvironment,
            "package");
        var report = MetadataCompatibilityAnalyzer.CreateUnavailableReport(
            null,
            targetEnvironment,
            _timeProvider.GetUtcNow(),
            request.ReleasePackageId.Value.ToString("D"),
            "persisted release package",
            "not found",
            "The requested metadata release package was not found.",
            scriptCount);
        return new PackageResolution(null, report);
    }

    private static string NormalizeTargetEnvironment(string targetEnvironment)
    {
        if (string.IsNullOrWhiteSpace(targetEnvironment))
        {
            throw new ArgumentException("TargetEnvironment is required.", nameof(targetEnvironment));
        }

        return targetEnvironment.Trim();
    }

    private static IReadOnlyList<MetadataDataScriptEntry> ValidateDataScripts(
        IReadOnlyList<MetadataDataScriptEntry>? dataScripts)
    {
        if (dataScripts is null)
        {
            throw new ArgumentException("DataScripts must be an array.", nameof(dataScripts));
        }

        if (dataScripts.Count > MaxDataScripts)
        {
            throw new ArgumentException($"DataScripts cannot contain more than {MaxDataScripts} entries.", nameof(dataScripts));
        }

        foreach (var script in dataScripts)
        {
            if (string.IsNullOrWhiteSpace(script.ScriptId))
            {
                throw new ArgumentException("DataScripts cannot contain a script with a blank scriptId.", nameof(dataScripts));
            }

            if (script.DeclaredOperations is null)
            {
                throw new ArgumentException("Data script declaredOperations must be an array.", nameof(dataScripts));
            }

            ValidateContract(script.BeforeContract, script.ScriptId, "beforeContract");
            ValidateContract(script.AfterContract, script.ScriptId, "afterContract");
        }

        return dataScripts;
    }

    private static void ValidateContract(
        MetadataDataScriptContract? contract,
        string scriptId,
        string propertyName)
    {
        if (contract is null)
        {
            return;
        }

        if (contract.Resources is null)
        {
            throw new ArgumentException($"Data script '{scriptId}' {propertyName}.resources must be an array.", propertyName);
        }

        var fieldCount = 0;
        foreach (var resource in contract.Resources)
        {
            if (string.IsNullOrWhiteSpace(resource.SemanticId))
            {
                throw new ArgumentException($"Data script '{scriptId}' contains a contract resource with a blank semanticId.", propertyName);
            }

            if (resource.Fields is null)
            {
                throw new ArgumentException($"Data script '{scriptId}' contract fields must be an array.", propertyName);
            }

            fieldCount += resource.Fields.Count;
            if (fieldCount > MaxScriptFields)
            {
                throw new ArgumentException(
                    $"Data script '{scriptId}' cannot declare more than {MaxScriptFields} contract fields.",
                    propertyName);
            }
        }
    }

    private static void TagReport(Activity? activity, MetadataCompatibilityReport report)
    {
        activity?.SetTag("metadata.status", report.Status.ToString());
        activity?.SetTag("metadata.finding.count", report.Findings.Count);
        activity?.SetTag("metadata.uncovered_error.count", report.UncoveredErrorCount);
        activity?.SetTag("metadata.covered_error.count", report.CoveredErrorCount);
        activity?.SetTag("metadata.dependent.count", report.AffectedDependents.Count);
        if (report.TargetRevision is { } targetRevision)
        {
            activity?.SetTag("metadata.target_revision", targetRevision);
        }
    }

    private sealed record PackageResolution(
        MetadataReleasePackage? Package,
        MetadataCompatibilityReport? Report);
}

internal static partial class MetadataCompatibilityPrevalidationLog
{
    [LoggerMessage(
        EventId = 116400,
        Level = LogLevel.Information,
        Message = "Metadata compatibility prevalidation started for target environment {TargetEnvironment} with {ScriptCount} scripts.")]
    public static partial void Started(
        ILogger logger,
        string targetEnvironment,
        int scriptCount);

    [LoggerMessage(
        EventId = 116401,
        Level = LogLevel.Warning,
        Message = "Metadata compatibility prevalidation state unavailable for package {PackageId} source {SourceEnvironment} target {TargetEnvironment}: {StateKind}.")]
    public static partial void StateUnavailable(
        ILogger logger,
        Guid packageId,
        string sourceEnvironment,
        string targetEnvironment,
        string stateKind);

    [LoggerMessage(
        EventId = 116402,
        Level = LogLevel.Information,
        Message = "Metadata compatibility prevalidation completed for package {PackageId} target {TargetEnvironment}: {Status}, findings={FindingCount}, uncoveredErrors={UncoveredErrorCount}, coveredErrors={CoveredErrorCount}, dependents={DependentCount}.")]
    public static partial void Completed(
        ILogger logger,
        Guid packageId,
        string targetEnvironment,
        MetadataCompatibilityStatus status,
        int findingCount,
        int uncoveredErrorCount,
        int coveredErrorCount,
        int dependentCount);
}
