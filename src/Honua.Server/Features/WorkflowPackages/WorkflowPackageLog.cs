// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.WorkflowPackages;

internal static partial class WorkflowPackageLog
{
    [LoggerMessage(118500, LogLevel.Information, "Workflow node registry listed {NodeCount} nodes with version {RegistryVersion}")]
    public static partial void NodeRegistryListed(ILogger logger, int nodeCount, string registryVersion);

    [LoggerMessage(118501, LogLevel.Warning, "Workflow node provider {ProviderId} failed")]
    public static partial void NodeProviderFailed(ILogger logger, string providerId, Exception exception);

    [LoggerMessage(118502, LogLevel.Information, "Workflow package {PackageId} saved with {NodeCount} nodes")]
    public static partial void PackageSaved(ILogger logger, string packageId, int nodeCount);

    [LoggerMessage(118503, LogLevel.Information, "Workflow package {PackageId} version {Version} created with hash {PackageHash}")]
    public static partial void PackageVersionCreated(ILogger logger, string packageId, int version, string packageHash);

    [LoggerMessage(118504, LogLevel.Information, "Workflow package {PackageId} version {Version} dry run completed in estimated {EstimatedDurationSeconds}s")]
    public static partial void PackageDryRunCompleted(
        ILogger logger,
        string packageId,
        int version,
        int estimatedDurationSeconds);

    [LoggerMessage(118505, LogLevel.Information, "Workflow package {PackageId} version {Version} published as {PublicationId} targeting {Target}")]
    public static partial void PackagePublished(
        ILogger logger,
        string packageId,
        int version,
        string publicationId,
        string target);

    [LoggerMessage(118506, LogLevel.Information, "Workflow package {PackageId} version {Version} publication {PublicationId} started run {RunId}")]
    public static partial void PackagePublicationRunStarted(
        ILogger logger,
        string packageId,
        int version,
        string publicationId,
        string runId);
}
