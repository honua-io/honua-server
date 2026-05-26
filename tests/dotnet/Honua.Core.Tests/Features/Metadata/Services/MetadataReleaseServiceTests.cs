// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.Json;
using Honua.Core.Features.Console.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Metadata.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Metadata.Services;

[Protocol(Protocols.TestQuality)]
[Operation(Operations.Metadata)]
public sealed class MetadataReleaseServiceTests
{
    [UnitTest]
    public async Task GetSemanticInventoryAsync_WhenObserved_EmitsRegisteredMetadataActivity()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "Honua.Core.Metadata",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => activities.Add(activity),
        };
        ActivitySource.AddActivityListener(listener);
        var service = CreateService(BuildGraph("dev", 41, MetadataV2ResourceType.Map));

        await service.GetSemanticInventoryAsync(
            "dev",
            new MetadataSemanticInventoryFilter
            {
                ArtifactKind = MetadataSemanticArtifactKind.Resource,
            });

        activities.Should().Contain(activity =>
            activity.Source.Name == "Honua.Core.Metadata" &&
            activity.OperationName == "honua.metadata.release.inventory" &&
            activity.TagObjects.Any(tag => tag.Key == "metadata.inventory.count" && Equals(tag.Value, 1)));
    }

    [UnitTest]
    public async Task GetSemanticInventoryAsync_WithKindAndResourceTypeFilters_ReturnsRevisionStampedEntries()
    {
        var service = CreateService(
            BuildGraph("dev", 41, MetadataV2ResourceType.Map),
            BuildGraph("staging", 7, MetadataV2ResourceType.Map));

        var response = await service.GetSemanticInventoryAsync(
            "dev",
            new MetadataSemanticInventoryFilter
            {
                ArtifactKind = MetadataSemanticArtifactKind.Resource,
                ResourceType = MetadataV2ResourceType.Map,
            });

        response.Should().NotBeNull();
        response!.Environment.Should().Be("dev");
        response.Revision.Should().Be(41);
        response.ETag.Should().Be("etag-dev-41");
        response.Entries.Should().ContainSingle(entry =>
            entry.SemanticId == "res.parcels" &&
            entry.ArtifactKind == MetadataSemanticArtifactKind.Resource &&
            entry.ResourceType == MetadataV2ResourceType.Map &&
            entry.ContentVersionId == "content-v1");
    }

    [UnitTest]
    public async Task GetEnvironmentBindingsAsync_WithMissingAndUnavailableStates_ScrubsConnectionOptions()
    {
        var service = CreateService(
            BuildGraph("dev", 41, MetadataV2ResourceType.FeatureDataset),
            BuildGraph("staging", 7, MetadataV2ResourceType.FeatureDataset));

        var response = await service.GetEnvironmentBindingsAsync(new MetadataEnvironmentBindingsRequest
        {
            Environments = ["dev", "prod"],
            SemanticIds = ["res.parcels", "res.missing"],
        });

        response.Bindings.Should().Contain(binding =>
            binding.Environment == "dev" &&
            binding.SemanticId == "res.parcels" &&
            binding.State == MetadataEnvironmentBindingState.Bound &&
            binding.Revision == 41 &&
            binding.Connection!.SecretRef == "aws-sm://honua/dev/parcels");
        response.Bindings.Should().Contain(binding =>
            binding.Environment == "dev" &&
            binding.SemanticId == "res.missing" &&
            binding.State == MetadataEnvironmentBindingState.Missing);
        response.Bindings.Should().Contain(binding =>
            binding.Environment == "prod" &&
            binding.State == MetadataEnvironmentBindingState.EnvironmentUnavailable);

        var json = JsonSerializer.Serialize(
            response,
            MetadataReleaseJsonContext.Default.MetadataEnvironmentBindingsResponse);
        json.Should().Contain("aws-sm://honua/dev/parcels");
        json.Should().NotContain("super-secret-password");
        json.Should().NotContain("connectionString");
    }

    [UnitTest]
    public async Task GetEnvironmentBindingsAsync_WithNullRequiredArrays_ThrowsValidationError()
    {
        var service = CreateService();

        var nullEnvironments = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetEnvironmentBindingsAsync(new MetadataEnvironmentBindingsRequest
            {
                Environments = null!,
                SemanticIds = ["res.parcels"],
            }));
        nullEnvironments.Message.Should().Contain("Environments must be an array.");

        var nullSemanticIds = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetEnvironmentBindingsAsync(new MetadataEnvironmentBindingsRequest
            {
                Environments = ["dev"],
                SemanticIds = null!,
            }));
        nullSemanticIds.Message.Should().Contain("SemanticIds must be an array.");
    }

    [UnitTest]
    public async Task CreateReleasePackageAsync_WithSourceAndTarget_CapturesDesiredAndCurrentRevisions()
    {
        var service = CreateService(
            BuildGraph("dev", 41, MetadataV2ResourceType.FeatureDataset),
            BuildGraph("staging", 7, MetadataV2ResourceType.FeatureDataset));

        var package = await service.CreateReleasePackageAsync(
            new CreateMetadataReleasePackageRequest
            {
                Title = "Promote parcels",
                SourceEnvironment = "dev",
                TargetEnvironments = ["staging"],
                SemanticIds = ["res.parcels"],
                Provenance =
                [
                    new ConsoleProvenanceRef
                    {
                        Kind = "console-content",
                        ItemId = "content.parcels",
                        Rel = "derived-from",
                    },
                ],
            },
            "user-1");

        package.SourceEnvironment.Should().Be("dev");
        package.SourceRevision.Should().Be(41);
        package.SourceEtag.Should().Be("etag-dev-41");
        package.TargetEnvironments.Should().Equal("staging");
        package.CreatedBy.Should().Be("user-1");
        var entry = package.Entries.Should().ContainSingle().Subject;
        entry.SemanticId.Should().Be("res.parcels");
        entry.DesiredMetadataRevision.Should().Be(41);
        entry.DesiredContentVersionId.Should().Be("content-v1");
        entry.DesiredProvenance.Should().ContainSingle(p => p.ItemId == "content.parcels");
        entry.TargetStates.Should().ContainSingle(state =>
            state.Environment == "staging" &&
            state.CurrentMetadataRevision == 7 &&
            state.BindingState == MetadataEnvironmentBindingState.Bound);
    }

    [UnitTest]
    public async Task CreateReleasePackageAsync_WithDesiredRevision_ResolvesArtifactsFromRequestedSourceSnapshot()
    {
        var service = CreateService(
            BuildGraph("dev", 40, MetadataV2ResourceType.FeatureDataset, "content-v40"),
            BuildGraph("dev", 41, MetadataV2ResourceType.FeatureDataset, "content-v41"),
            BuildGraph("staging", 7, MetadataV2ResourceType.FeatureDataset));

        var package = await service.CreateReleasePackageAsync(
            new CreateMetadataReleasePackageRequest
            {
                SourceEnvironment = "dev",
                TargetEnvironments = ["staging"],
                SemanticIds = ["res.parcels"],
                DesiredRevision = 40,
            },
            "user-1");

        package.SourceRevision.Should().Be(40);
        package.SourceEtag.Should().Be("etag-dev-40");
        package.Entries.Should().ContainSingle()
            .Subject.DesiredContentVersionId.Should().Be("content-v40");
    }

    [UnitTest]
    public async Task CreateReleasePackageAsync_WithFieldAndMissingTarget_PersistsSourceFieldIdentity()
    {
        var service = CreateService(
            BuildGraph("dev", 41, MetadataV2ResourceType.FeatureDataset),
            BuildGraph("staging", 7, MetadataV2ResourceType.FeatureDataset, includeSchemaField: false));

        var package = await service.CreateReleasePackageAsync(
            new CreateMetadataReleasePackageRequest
            {
                SourceEnvironment = "dev",
                TargetEnvironments = ["staging"],
                SemanticIds = ["field.parcels.apn"],
            },
            "user-1");

        var entry = package.Entries.Should().ContainSingle().Subject;
        entry.SemanticId.Should().Be("field.parcels.apn");
        entry.ArtifactKind.Should().Be(MetadataSemanticArtifactKind.Field);
        entry.SourceField.Should().NotBeNull();
        entry.SourceField!.SemanticId.Should().Be("field.parcels.apn");
        entry.SourceField.ParentResourceId.Should().Be("res.parcels");
        entry.SourceField.FieldName.Should().Be("apn");
        entry.SourceField.FieldType.Should().Be("string");
        var targetState = entry.TargetStates.Should().ContainSingle().Subject;
        targetState.Environment.Should().Be("staging");
        targetState.CurrentMetadataRevision.Should().Be(7);
        targetState.BindingState.Should().Be(MetadataEnvironmentBindingState.Missing);
        targetState.BindingSummary.Should().NotBeNull();
        targetState.BindingSummary!.Field.Should().BeNull();

        var manifest = await service.GetGitOpsManifestAsync(package.PackageId);
        var manifestEntry = manifest!.Spec.Entries.Should().ContainSingle().Subject;
        manifestEntry.SourceField.Should().NotBeNull();
        manifestEntry.SourceField!.ParentResourceId.Should().Be("res.parcels");
        manifestEntry.SourceField.FieldName.Should().Be("apn");

        var json = JsonSerializer.Serialize(
            manifest,
            MetadataReleaseJsonContext.Default.GitOpsMetadataReleaseManifest);
        json.Should().Contain("\"sourceField\"");
        json.Should().Contain("\"parentResourceId\":\"res.parcels\"");
        json.Should().Contain("\"fieldName\":\"apn\"");
    }

    [UnitTest]
    public async Task CreateReleasePackageAsync_WithGeneratedTitleKey_CreatesUniquePackageNames()
    {
        var service = CreateService(
            BuildGraph("dev", 41, MetadataV2ResourceType.FeatureDataset),
            BuildGraph("staging", 7, MetadataV2ResourceType.FeatureDataset));

        var first = await service.CreateReleasePackageAsync(
            new CreateMetadataReleasePackageRequest
            {
                Title = "Promote parcels",
                SourceEnvironment = "dev",
                TargetEnvironments = ["staging"],
                SemanticIds = ["res.parcels"],
            },
            "user-1");
        var second = await service.CreateReleasePackageAsync(
            new CreateMetadataReleasePackageRequest
            {
                Title = "Promote parcels",
                SourceEnvironment = "dev",
                TargetEnvironments = ["staging"],
                SemanticIds = ["res.parcels"],
            },
            "user-1");

        first.Metadata.Name.Should().StartWith("promote-parcels-");
        second.Metadata.Name.Should().StartWith("promote-parcels-");
        second.Metadata.Name.Should().NotBe(first.Metadata.Name);
    }

    [UnitTest]
    public async Task CreateReleasePackageAsync_WithRequestContentVersion_KeepsArtifactContentVersionWhenPresent()
    {
        var service = CreateService(
            BuildGraph("dev", 41, MetadataV2ResourceType.FeatureDataset),
            BuildGraph("staging", 7, MetadataV2ResourceType.FeatureDataset));

        var package = await service.CreateReleasePackageAsync(
            new CreateMetadataReleasePackageRequest
            {
                SourceEnvironment = "dev",
                TargetEnvironments = ["staging"],
                SemanticIds = ["res.parcels"],
                DesiredContentVersionId = "request-content-v2",
            },
            "user-1");

        package.Entries.Should().ContainSingle()
            .Subject.DesiredContentVersionId.Should().Be("content-v1");
    }

    [UnitTest]
    public async Task CreateReleasePackageAsync_WithRequestContentVersion_UsesRequestFallbackWhenArtifactHasNone()
    {
        var service = CreateService(
            BuildGraph("dev", 41, MetadataV2ResourceType.FeatureDataset),
            BuildGraph("staging", 7, MetadataV2ResourceType.FeatureDataset));

        var package = await service.CreateReleasePackageAsync(
            new CreateMetadataReleasePackageRequest
            {
                SourceEnvironment = "dev",
                TargetEnvironments = ["staging"],
                SemanticIds = ["pub.parcels"],
                DesiredContentVersionId = "request-content-v2",
            },
            "user-1");

        package.Entries.Should().ContainSingle()
            .Subject.DesiredContentVersionId.Should().Be("request-content-v2");
    }

    [UnitTest]
    public async Task CreateReleasePackageAsync_WithNullRequestArrays_ThrowsValidationError()
    {
        var service = CreateService(
            BuildGraph("dev", 41, MetadataV2ResourceType.FeatureDataset),
            BuildGraph("staging", 7, MetadataV2ResourceType.FeatureDataset));

        var nullTargets = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateReleasePackageAsync(
                new CreateMetadataReleasePackageRequest
                {
                    SourceEnvironment = "dev",
                    TargetEnvironments = null!,
                    SemanticIds = ["res.parcels"],
                },
                "user-1"));
        nullTargets.Message.Should().Contain("TargetEnvironments must be an array.");

        var nullSemanticIds = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateReleasePackageAsync(
                new CreateMetadataReleasePackageRequest
                {
                    SourceEnvironment = "dev",
                    TargetEnvironments = ["staging"],
                    SemanticIds = null!,
                },
                "user-1"));
        nullSemanticIds.Message.Should().Contain("SemanticIds must be an array.");

        var nullProvenance = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateReleasePackageAsync(
                new CreateMetadataReleasePackageRequest
                {
                    SourceEnvironment = "dev",
                    TargetEnvironments = ["staging"],
                    SemanticIds = ["res.parcels"],
                    Provenance = null!,
                },
                "user-1"));
        nullProvenance.Message.Should().Contain("Provenance must be an array.");
    }

    [UnitTest]
    public async Task GetGitOpsManifestAsync_ForPersistedPackage_UsesSourceGeneratedSecretSafeShape()
    {
        var service = CreateService(
            BuildGraph("dev", 42, MetadataV2ResourceType.FeatureDataset),
            BuildGraph("staging", 7, MetadataV2ResourceType.FeatureDataset));
        var package = await service.CreateReleasePackageAsync(
            new CreateMetadataReleasePackageRequest
            {
                SourceEnvironment = "dev",
                TargetEnvironments = ["staging"],
                SemanticIds = ["pub.parcels"],
                DesiredRevision = 42,
                DesiredContentVersionId = "content-v2",
            },
            "user-1");

        var manifest = await service.GetGitOpsManifestAsync(package.PackageId);

        manifest.Should().NotBeNull();
        manifest!.ApiVersion.Should().Be(MetadataV2Constants.ApiVersion);
        manifest.Kind.Should().Be("MetadataReleasePackage");
        manifest.Spec.Source.Revision.Should().Be(42);
        manifest.Spec.Entries.Should().ContainSingle(entry =>
            entry.SemanticId == "pub.parcels" &&
            entry.DesiredMetadataRevision == 42 &&
            entry.DesiredContentVersionId == "content-v2");

        var json = JsonSerializer.Serialize(
            manifest,
            MetadataReleaseJsonContext.Default.GitOpsMetadataReleaseManifest);
        json.Should().Contain("MetadataReleasePackage");
        json.Should().NotContain("super-secret-password");
    }

    private static MetadataReleaseService CreateService(params MetadataV2Graph[] graphs)
    {
        var reader = new StaticEnvironmentReader(graphs);
        return new MetadataReleaseService(
            reader,
            new InMemoryMetadataReleasePackageStore(),
            TimeProvider.System,
            NullLogger<MetadataReleaseService>.Instance);
    }

    private static MetadataV2Graph BuildGraph(
        string environment,
        long revision,
        MetadataV2ResourceType resourceType,
        string contentVersionId = "content-v1",
        bool includeSchemaField = true)
    {
        var passwordOption = JsonSerializer.Deserialize<JsonElement>("\"super-secret-password\"");
        return new MetadataV2Graph
        {
            Environment = environment,
            Revision = revision,
            GeneratedAt = DateTimeOffset.UtcNow,
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata
                    {
                        Id = "res.parcels",
                        Name = "parcels",
                        Title = "Parcels",
                        Generation = revision,
                        Annotations = new Dictionary<string, string>
                        {
                            ["honua.io/content-version-id"] = contentVersionId,
                        },
                    },
                    Type = resourceType,
                    StorageBindingIds = ["storage.parcels"],
                    PrimaryStorageBindingId = "storage.parcels",
                    SchemaFields = BuildSchemaFields(includeSchemaField),
                    PolicyIds = ["policy.read-parcels"],
                },
            ],
            Connections =
            [
                new MetadataV2Connection
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "conn.parcels", Name = "parcels-db" },
                    Type = MetadataV2ConnectionType.Database,
                    Provider = "postgres",
                    Endpoint = new Uri("https://metadata.example.test/connections/parcels"),
                    SecretRef = "aws-sm://honua/dev/parcels",
                    Options = new Dictionary<string, JsonElement>
                    {
                        ["connectionString"] = passwordOption,
                    },
                },
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "storage.parcels", Name = "storage.parcels" },
                    ResourceId = "res.parcels",
                    ConnectionId = "conn.parcels",
                    StorageType = MetadataV2StorageType.RelationalTable,
                    Locator = $"{environment}_schema.parcels",
                },
            ],
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "svc.features", Name = "features" },
                    ServiceType = MetadataV2ServiceType.OgcApiFeatures,
                    Route = $"/{environment}/ogc/features",
                    PublicationIds = ["pub.parcels"],
                },
            ],
            Publications =
            [
                new MetadataV2Publication
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "pub.parcels", Name = "parcels" },
                    ResourceId = "res.parcels",
                    ServiceId = "svc.features",
                    StorageBindingId = "storage.parcels",
                    PublicationType = MetadataV2PublicationType.OgcCollection,
                    Path = "parcels",
                    ServiceLocalId = "parcels",
                },
            ],
            Policies =
            [
                new MetadataV2Policy
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "policy.read-parcels", Name = "read-parcels" },
                    Engine = "rbac",
                    Effect = "allow",
                },
            ],
            Roles =
            [
                new MetadataV2Role
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "role.publisher", Name = "publisher" },
                    Permissions = ["metadata.read", "metadata.write"],
                    PolicyIds = ["policy.read-parcels"],
                },
            ],
        };
    }

    private static MetadataV2Field[] BuildSchemaFields(bool includeSchemaField)
    {
        if (!includeSchemaField)
        {
            return Array.Empty<MetadataV2Field>();
        }

        return
        [
            new MetadataV2Field
            {
                SemanticId = "field.parcels.apn",
                Name = "apn",
                Type = MetadataV2FieldType.String,
            },
        ];
    }

    private sealed class StaticEnvironmentReader : IMetadataV2EnvironmentSnapshotReader
    {
        private readonly Dictionary<string, MetadataV2GraphSnapshot> _currentSnapshots;
        private readonly Dictionary<string, Dictionary<long, MetadataV2GraphSnapshot>> _snapshotsByRevision;

        public StaticEnvironmentReader(IEnumerable<MetadataV2Graph> graphs)
        {
            _snapshotsByRevision = new Dictionary<string, Dictionary<long, MetadataV2GraphSnapshot>>(
                StringComparer.OrdinalIgnoreCase);
            _currentSnapshots = new Dictionary<string, MetadataV2GraphSnapshot>(StringComparer.OrdinalIgnoreCase);

            foreach (var graph in graphs)
            {
                var snapshot = new MetadataV2GraphSnapshot(
                    graph,
                    $"etag-{graph.Environment}-{graph.Revision}",
                    DateTimeOffset.UtcNow);
                if (!_snapshotsByRevision.TryGetValue(graph.Environment, out var revisions))
                {
                    revisions = new Dictionary<long, MetadataV2GraphSnapshot>();
                    _snapshotsByRevision[graph.Environment] = revisions;
                }

                revisions[graph.Revision] = snapshot;
                if (!_currentSnapshots.TryGetValue(graph.Environment, out var current) || graph.Revision > current.Revision)
                {
                    _currentSnapshots[graph.Environment] = snapshot;
                }
            }
        }

        public ValueTask<MetadataV2GraphSnapshot?> GetCurrentAsync(
            string environment,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(
                _currentSnapshots.TryGetValue(environment, out var snapshot) ? snapshot : null);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(
            string environment,
            long revision,
            CancellationToken cancellationToken = default)
        {
            var snapshot = _snapshotsByRevision.TryGetValue(environment, out var revisions) &&
                revisions.TryGetValue(revision, out var requested)
                ? requested
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
                if (_currentSnapshots.TryGetValue(environment, out var snapshot))
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
}
