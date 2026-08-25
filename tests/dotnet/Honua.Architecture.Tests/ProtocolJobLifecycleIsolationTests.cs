// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Prevents OGC Processes and GPServer adapters from taking ownership of canonical runtime
/// stores, queues, worker notifiers, or compute backends.
/// </summary>
[Trait("Category", "Architecture")]
public sealed class ProtocolJobLifecycleIsolationTests
{
    private static readonly string[] BannedRuntimeTypes =
    [
        "IExecutionJobStore",
        "IJobQueue",
        "IJobCancellationNotifier",
        "IBatchComputeBackend",
        "ExecutionJobCancellationHelper",
        "ExecutionJobSubmissionHelper",
        "CancelAbandonedJobAsync",
        "CancelOrphanedAsync",
        "GetJobForTerminalAsync",
        "GetJobResultsForTerminalAsync",
    ];

    [ArchitectureTest]
    public void ProtocolJobAdapters_ShouldDependOnlyOnCanonicalLifecycleServices()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var adapterDirectories = new[]
        {
            ArchitectureTestHelpers.CombinePath(root, "src", "Honua.Protocols.OgcApi", "Processes"),
            ArchitectureTestHelpers.CombinePath(root, "src", "Honua.Protocols.GeoServices", "GPServer"),
        };

        var violations = adapterDirectories
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            .SelectMany(path => BannedRuntimeTypes
                .Where(type => File.ReadAllText(path).Contains(type, StringComparison.Ordinal))
                .Select(type => $"{Path.GetRelativePath(root, path)} -> {type}"))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        violations.Should().BeEmpty(
            "protocol job adapters must translate typed IGeoprocessingJobTerminalService outcomes " +
            "instead of coordinating stores, queues, worker notifiers, or compute backends");
    }

    [ArchitectureTest]
    public void ProtocolProjects_ShouldNotReference_JobInfrastructureProject()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var protocolProjects = new[]
        {
            ArchitectureTestHelpers.CombinePath(root, "src", "Honua.Protocols.OgcApi", "Honua.Protocols.OgcApi.csproj"),
            ArchitectureTestHelpers.CombinePath(root, "src", "Honua.Protocols.GeoServices", "Honua.Protocols.GeoServices.csproj"),
        };

        var violations = protocolProjects
            .Where(project => ArchitectureTestHelpers.DirectProjectReferenceNames(project)
                .Contains("Honua.Jobs", StringComparer.Ordinal))
            .Select(project => Path.GetRelativePath(root, project))
            .ToArray();

        violations.Should().BeEmpty(
            "OGC Processes and GPServer consume the canonical geoprocessing lifecycle service, " +
            "so their protocol projects must not reference Honua.Jobs directly");
    }
}
