// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
    public async Task GetGitOpsManifestAsync_ForPersistedPackage_UsesSourceGeneratedSecretSafeShape()
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
                DesiredRevision = 42,
                DesiredContentVersionId = "content-v2",
            },
            "user-1");

        var manifest = await service.GetGitOpsManifestAsync(package.PackageId);

        manifest.Should().NotBeNull();
        manifest!.ApiVersion.Should().Be(MetadataV2Constants.ApiVersion);
        manifest.Kind.Should().Be("MetadataReleasePackage");
        manifest.Spec.Source.Revision.Should().Be(41);
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
        MetadataV2ResourceType resourceType)
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
                            ["honua.io/content-version-id"] = "content-v1",
                        },
                    },
                    Type = resourceType,
                    StorageBindingIds = ["storage.parcels"],
                    PrimaryStorageBindingId = "storage.parcels",
                    SchemaFields =
                    [
                        new MetadataV2Field
                        {
                            SemanticId = "field.parcels.apn",
                            Name = "apn",
                            Type = "string",
                        },
                    ],
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

    private sealed class StaticEnvironmentReader : IMetadataV2EnvironmentSnapshotReader
    {
        private readonly Dictionary<string, MetadataV2GraphSnapshot> _snapshots;

        public StaticEnvironmentReader(IEnumerable<MetadataV2Graph> graphs)
        {
            _snapshots = graphs.ToDictionary(
                static graph => graph.Environment,
                static graph => new MetadataV2GraphSnapshot(
                    graph,
                    $"etag-{graph.Environment}-{graph.Revision}",
                    DateTimeOffset.UtcNow),
                StringComparer.OrdinalIgnoreCase);
        }

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
}
