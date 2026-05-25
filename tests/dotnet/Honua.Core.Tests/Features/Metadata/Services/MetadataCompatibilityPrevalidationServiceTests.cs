// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Metadata.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Metadata.Services;

[Protocol(Protocols.TestQuality)]
[Operation(Operations.Validation)]
public sealed class MetadataCompatibilityPrevalidationServiceTests
{
    private static readonly DateTimeOffset GeneratedAt = new(2026, 5, 24, 12, 0, 0, TimeSpan.Zero);

    [UnitTest]
    public async Task PrevalidateAsync_WithPersistedPackageId_LoadsSnapshotsAndEmitsActivity()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "Honua.Core.Metadata",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => activities.Add(activity),
        };
        ActivitySource.AddActivityListener(listener);

        var store = new InMemoryMetadataReleasePackageStore();
        var package = BuildPackage();
        await store.CreateAsync(package);
        var service = CreateService(
            store,
            BuildGraph("dev", 41, includeField: true),
            BuildGraph("staging", 7, includeField: false));

        var report = await service.PrevalidateAsync(new MetadataCompatibilityPrevalidationRequest
        {
            ReleasePackageId = package.PackageId,
            TargetEnvironment = "staging",
        });

        report.Status.Should().Be(MetadataCompatibilityStatus.Blocked);
        report.ReleasePackageId.Should().Be(package.PackageId);
        report.Findings.Should().Contain(finding => finding.Code == MetadataCompatibilityCode.FieldMissing);
        activities.Should().Contain(activity =>
            activity.OperationName == "honua.metadata.compatibility.prevalidate" &&
            activity.TagObjects.Any(tag => tag.Key == "metadata.finding.count"));
    }

    [UnitTest]
    public async Task PrevalidateAsync_WithUnavailableTarget_ReturnsUnknownReport()
    {
        var service = CreateService(new InMemoryMetadataReleasePackageStore(), BuildGraph("dev", 41, includeField: true));

        var report = await service.PrevalidateAsync(new MetadataCompatibilityPrevalidationRequest
        {
            ReleasePackage = BuildPackage(),
            TargetEnvironment = "prod",
        });

        report.Status.Should().Be(MetadataCompatibilityStatus.Unknown);
        report.CanPromote.Should().BeFalse();
        report.Findings.Should().ContainSingle(finding => finding.Code == MetadataCompatibilityCode.StateUnavailable);
    }

    [UnitTest]
    public async Task PrevalidateAsync_WithBothPackageInputs_ThrowsValidationError()
    {
        var service = CreateService(new InMemoryMetadataReleasePackageStore(), BuildGraph("dev", 41, includeField: true));

        var act = () => service.PrevalidateAsync(new MetadataCompatibilityPrevalidationRequest
        {
            ReleasePackageId = Guid.NewGuid(),
            ReleasePackage = BuildPackage(),
            TargetEnvironment = "staging",
        });

        var exception = await Assert.ThrowsAsync<ArgumentException>(act);
        exception.Message.Should().Contain("Exactly one of ReleasePackageId or ReleasePackage is required.");
    }

    private static MetadataCompatibilityPrevalidationService CreateService(
        IMetadataReleasePackageStore store,
        params MetadataV2Graph[] graphs)
        => new(
            new StaticEnvironmentReader(graphs),
            store,
            new FakeTimeProvider(GeneratedAt),
            NullLogger<MetadataCompatibilityPrevalidationService>.Instance);

    private static MetadataReleasePackage BuildPackage()
        => new()
        {
            PackageId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "pkg.parcels",
                Name = "promote-parcels",
            },
            SourceEnvironment = "dev",
            SourceRevision = 41,
            SourceEtag = "etag-dev-41",
            TargetEnvironments = ["staging"],
            Entries =
            [
                new MetadataReleaseEntry
                {
                    SemanticId = "res.parcels",
                    ArtifactKind = MetadataSemanticArtifactKind.Resource,
                    DesiredMetadataRevision = 41,
                },
            ],
            CreatedBy = "tester",
            CreatedAt = GeneratedAt,
            UpdatedAt = GeneratedAt,
        };

    private static MetadataV2Graph BuildGraph(string environment, long revision, bool includeField)
        => new()
        {
            Environment = environment,
            Revision = revision,
            GeneratedAt = GeneratedAt,
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "res.parcels", Name = "parcels" },
                    Type = MetadataV2ResourceType.FeatureDataset,
                    StorageBindingIds = ["storage.parcels"],
                    PrimaryStorageBindingId = "storage.parcels",
                    SchemaFields = includeField
                        ?
                        [
                            new MetadataV2Field
                            {
                                SemanticId = "field.parcels.apn",
                                Name = "apn",
                                Type = "string",
                            },
                        ]
                        : Array.Empty<MetadataV2Field>(),
                },
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "storage.parcels", Name = "storage.parcels" },
                    ResourceId = "res.parcels",
                    StorageType = MetadataV2StorageType.RelationalTable,
                    Locator = "shared.parcels",
                },
            ],
        };

    private sealed class StaticEnvironmentReader(IEnumerable<MetadataV2Graph> graphs) : IMetadataV2EnvironmentSnapshotReader
    {
        private readonly Dictionary<string, MetadataV2GraphSnapshot> _snapshots = graphs.ToDictionary(
            static graph => graph.Environment,
            static graph => new MetadataV2GraphSnapshot(
                graph,
                $"etag-{graph.Environment}-{graph.Revision}",
                GeneratedAt),
            StringComparer.OrdinalIgnoreCase);

        public ValueTask<MetadataV2GraphSnapshot?> GetCurrentAsync(
            string environment,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(
                _snapshots.TryGetValue(environment, out var snapshot) ? snapshot : null);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(
            string environment,
            long revision,
            CancellationToken cancellationToken = default)
        {
            var snapshot = _snapshots.TryGetValue(environment, out var current) && current.Revision == revision
                ? current
                : null;
            return ValueTask.FromResult(snapshot);
        }

        public async IAsyncEnumerable<MetadataV2EnvironmentRevision> ListCurrentRevisionsAsync(
            IReadOnlyList<string> environments,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var environment in environments)
            {
                if (_snapshots.TryGetValue(environment, out var snapshot))
                {
                    yield return new MetadataV2EnvironmentRevision
                    {
                        Environment = snapshot.Graph.Environment,
                        Revision = snapshot.Revision,
                        ETag = snapshot.Etag,
                        ActivatedAt = snapshot.LoadedAt,
                    };
                }
            }
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
