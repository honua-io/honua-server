// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Metadata.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Metadata)]
public sealed class MetadataReleaseEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public MetadataReleaseEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IMetadataV2EnvironmentSnapshotReader>();
                services.RemoveAll<IMetadataReleasePackageStore>();
                services.AddSingleton<IMetadataV2EnvironmentSnapshotReader>(
                    new StaticEnvironmentReader(
                        BuildGraph("dev", 41),
                        BuildGraph("staging", 7, includeSchemaField: false)));
                services.AddSingleton<IMetadataReleasePackageStore, CancellationAwareReleasePackageStore>();
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/metadata/environments/{environment}/inventory")]
    public async Task GetInventory_WithFilters_ReturnsRevisionStampedSemanticInventory()
    {
        var response = await _client.GetAsync(
            "/api/v1/admin/metadata/environments/dev/inventory?artifactKind=resource&resourceType=map");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadAsStringAsync();
        var inventory = JsonSerializer.Deserialize(
            payload,
            MetadataReleaseJsonContext.Default.MetadataSemanticInventoryResponse);

        inventory.Should().NotBeNull();
        inventory!.Environment.Should().Be("dev");
        inventory.Revision.Should().Be(41);
        inventory.ETag.Should().Be("etag-dev-41");
        inventory.Entries.Should().ContainSingle(entry =>
            entry.SemanticId == "res.parcels" &&
            entry.ArtifactKind == MetadataSemanticArtifactKind.Resource &&
            entry.ResourceType == MetadataV2ResourceType.Map);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/environment-bindings/query")]
    public async Task QueryEnvironmentBindings_WithUnavailableEnvironment_ReturnsSecretSafeStates()
    {
        var body = JsonSerializer.Serialize(
            new MetadataEnvironmentBindingsRequest
            {
                Environments = ["dev", "prod"],
                SemanticIds = ["res.parcels"],
            },
            MetadataReleaseJsonContext.Default.MetadataEnvironmentBindingsRequest);

        var response = await _client.PostAsync(
            "/api/v1/admin/metadata/environment-bindings/query",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain("aws-sm://honua/dev/parcels");
        payload.Should().NotContain("super-secret-password");

        var bindings = JsonSerializer.Deserialize(
            payload,
            MetadataReleaseJsonContext.Default.MetadataEnvironmentBindingsResponse);
        bindings.Should().NotBeNull();
        bindings!.Bindings.Should().Contain(binding =>
            binding.Environment == "dev" &&
            binding.State == MetadataEnvironmentBindingState.Bound &&
            binding.Connection!.SecretRef == "aws-sm://honua/dev/parcels");
        bindings.Bindings.Should().Contain(binding =>
            binding.Environment == "prod" &&
            binding.State == MetadataEnvironmentBindingState.EnvironmentUnavailable);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/environment-bindings/query")]
    [Endpoint("POST /api/v1/admin/metadata/release-packages")]
    public async Task ReleaseRequestEndpoints_WithExplicitNullArrays_ReturnBadRequest()
    {
        (string Path, string Body, string ExpectedMessage)[] cases =
        [
            (
                "/api/v1/admin/metadata/environment-bindings/query",
                "{\"environments\":null,\"semanticIds\":[\"res.parcels\"]}",
                "Environments must be an array."),
            (
                "/api/v1/admin/metadata/environment-bindings/query",
                "{\"environments\":[\"dev\"],\"semanticIds\":null}",
                "SemanticIds must be an array."),
            (
                "/api/v1/admin/metadata/release-packages",
                "{\"sourceEnvironment\":\"dev\",\"targetEnvironments\":null,\"semanticIds\":[\"res.parcels\"]}",
                "TargetEnvironments must be an array."),
            (
                "/api/v1/admin/metadata/release-packages",
                "{\"sourceEnvironment\":\"dev\",\"targetEnvironments\":[\"staging\"],\"semanticIds\":null}",
                "SemanticIds must be an array."),
            (
                "/api/v1/admin/metadata/release-packages",
                "{\"sourceEnvironment\":\"dev\",\"targetEnvironments\":[\"staging\"],\"semanticIds\":[\"res.parcels\"],\"provenance\":null}",
                "Provenance must be an array."),
        ];

        foreach (var (path, body, expectedMessage) in cases)
        {
            var response = await _client.PostAsync(
                path,
                new StringContent(body, Encoding.UTF8, "application/json"));

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var payload = await response.Content.ReadAsStringAsync();
            payload.Should().Contain(expectedMessage);
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/release-packages")]
    [Endpoint("GET /api/v1/admin/metadata/release-packages/{packageId}")]
    [Endpoint("GET /api/v1/admin/metadata/release-packages/{packageId}/gitops-manifest")]
    public async Task ReleasePackageEndpoints_CreateReadAndExportGitOpsSafeManifest()
    {
        var body = JsonSerializer.Serialize(
            new CreateMetadataReleasePackageRequest
            {
                Title = "Promote parcels",
                SourceEnvironment = "dev",
                TargetEnvironments = ["staging"],
                SemanticIds = ["res.parcels"],
            },
            MetadataReleaseJsonContext.Default.CreateMetadataReleasePackageRequest);

        var createResponse = await _client.PostAsync(
            "/api/v1/admin/metadata/release-packages",
            new StringContent(body, Encoding.UTF8, "application/json"));

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var createPayload = await createResponse.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize(
            createPayload,
            MetadataReleaseJsonContext.Default.MetadataReleasePackage);
        created.Should().NotBeNull();
        created!.SourceRevision.Should().Be(41);
        created.Entries.Should().ContainSingle(entry =>
            entry.SemanticId == "res.parcels" &&
            entry.TargetStates.Single().CurrentMetadataRevision == 7);

        var getResponse = await _client.GetAsync(
            $"/api/v1/admin/metadata/release-packages/{created.PackageId:D}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var manifestResponse = await _client.GetAsync(
            $"/api/v1/admin/metadata/release-packages/{created.PackageId:D}/gitops-manifest");
        manifestResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var manifestPayload = await manifestResponse.Content.ReadAsStringAsync();
        manifestPayload.Should().Contain("MetadataReleasePackage");
        manifestPayload.Should().Contain("content-v1");
        manifestPayload.Should().NotContain("super-secret-password");

        var manifest = JsonSerializer.Deserialize(
            manifestPayload,
            MetadataReleaseJsonContext.Default.GitOpsMetadataReleaseManifest);
        manifest.Should().NotBeNull();
        manifest!.Spec.Source.Revision.Should().Be(41);
        manifest.Spec.Targets.Should().ContainSingle(target => target.Environment == "staging");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/release-packages")]
    [Endpoint("GET /api/v1/admin/metadata/release-packages/{packageId}/gitops-manifest")]
    public async Task ReleasePackageEndpoints_WithFieldAndMissingTarget_ExportsSourceFieldIdentity()
    {
        var body = JsonSerializer.Serialize(
            new CreateMetadataReleasePackageRequest
            {
                Title = "Promote parcel APN",
                SourceEnvironment = "dev",
                TargetEnvironments = ["staging"],
                SemanticIds = ["field.parcels.apn"],
            },
            MetadataReleaseJsonContext.Default.CreateMetadataReleasePackageRequest);

        var createResponse = await _client.PostAsync(
            "/api/v1/admin/metadata/release-packages",
            new StringContent(body, Encoding.UTF8, "application/json"));

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var createPayload = await createResponse.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize(
            createPayload,
            MetadataReleaseJsonContext.Default.MetadataReleasePackage);
        created.Should().NotBeNull();
        var entry = created!.Entries.Should().ContainSingle().Subject;
        entry.SourceField.Should().NotBeNull();
        entry.SourceField!.ParentResourceId.Should().Be("res.parcels");
        entry.SourceField.FieldName.Should().Be("apn");
        var targetState = entry.TargetStates.Should().ContainSingle().Subject;
        targetState.Environment.Should().Be("staging");
        targetState.BindingState.Should().Be(MetadataEnvironmentBindingState.Missing);

        var manifestResponse = await _client.GetAsync(
            $"/api/v1/admin/metadata/release-packages/{created.PackageId:D}/gitops-manifest");
        manifestResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var manifestPayload = await manifestResponse.Content.ReadAsStringAsync();
        manifestPayload.Should().Contain("\"sourceField\"");
        manifestPayload.Should().Contain("\"parentResourceId\":\"res.parcels\"");
        manifestPayload.Should().Contain("\"fieldName\":\"apn\"");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/release-packages")]
    public async Task CreateReleasePackage_WithRepeatedGeneratedTitleKeys_ReturnsCreatedPackages()
    {
        var body = JsonSerializer.Serialize(
            new CreateMetadataReleasePackageRequest
            {
                Title = "Promote parcels",
                SourceEnvironment = "dev",
                TargetEnvironments = ["staging"],
                SemanticIds = ["res.parcels"],
            },
            MetadataReleaseJsonContext.Default.CreateMetadataReleasePackageRequest);

        var firstResponse = await _client.PostAsync(
            "/api/v1/admin/metadata/release-packages",
            new StringContent(body, Encoding.UTF8, "application/json"));
        var secondResponse = await _client.PostAsync(
            "/api/v1/admin/metadata/release-packages",
            new StringContent(body, Encoding.UTF8, "application/json"));

        firstResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var firstPayload = await firstResponse.Content.ReadAsStringAsync();
        var secondPayload = await secondResponse.Content.ReadAsStringAsync();
        var first = JsonSerializer.Deserialize(
            firstPayload,
            MetadataReleaseJsonContext.Default.MetadataReleasePackage);
        var second = JsonSerializer.Deserialize(
            secondPayload,
            MetadataReleaseJsonContext.Default.MetadataReleasePackage);

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        first!.Metadata.Name.Should().StartWith("promote-parcels-");
        second!.Metadata.Name.Should().StartWith("promote-parcels-");
        second.Metadata.Name.Should().NotBe(first.Metadata.Name);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/release-packages")]
    public async Task CreateReleasePackage_WithDuplicateDefaultNamespacePackageKey_ReturnsConflict()
    {
        var body = JsonSerializer.Serialize(
            new CreateMetadataReleasePackageRequest
            {
                PackageKey = "duplicate-package-key",
                SourceEnvironment = "dev",
                TargetEnvironments = ["staging"],
                SemanticIds = ["res.parcels"],
            },
            MetadataReleaseJsonContext.Default.CreateMetadataReleasePackageRequest);
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var firstResponse = await _client.PostAsync(
            "/api/v1/admin/metadata/release-packages",
            content);

        firstResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var secondResponse = await _client.PostAsync(
            "/api/v1/admin/metadata/release-packages",
            new StringContent(body, Encoding.UTF8, "application/json"));

        secondResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var payload = await secondResponse.Content.ReadAsStringAsync();
        payload.Should().Contain("Metadata release package conflicts with an existing package.");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/release-packages")]
    public async Task CreateReleasePackage_WhenStoreCancellationOccurs_DoesNotMapToPackageCreateFailure()
    {
        var body = JsonSerializer.Serialize(
            new CreateMetadataReleasePackageRequest
            {
                PackageKey = "cancelled-package",
                SourceEnvironment = "dev",
                TargetEnvironments = ["staging"],
                SemanticIds = ["res.parcels"],
            },
            MetadataReleaseJsonContext.Default.CreateMetadataReleasePackageRequest);

        var response = await _client.PostAsync(
            "/api/v1/admin/metadata/release-packages",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.RequestTimeout);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().NotContain("Metadata release package could not be created.");
    }

    private static MetadataV2Graph BuildGraph(string environment, long revision, bool includeSchemaField = true)
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
                        Annotations = new Dictionary<string, string>
                        {
                            ["honua.io/content-version-id"] = "content-v1",
                        },
                    },
                    Type = MetadataV2ResourceType.Map,
                    StorageBindingIds = ["storage.parcels"],
                    PrimaryStorageBindingId = "storage.parcels",
                    SchemaFields = BuildSchemaFields(includeSchemaField),
                },
            ],
            Connections =
            [
                new MetadataV2Connection
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "conn.parcels", Name = "parcels-db" },
                    Type = MetadataV2ConnectionType.Database,
                    Provider = "postgres",
                    SecretRef = "aws-sm://honua/dev/parcels",
                    Options = new Dictionary<string, JsonElement>
                    {
                        ["password"] = passwordOption,
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
                Type = "string",
            },
        ];
    }

    private sealed class StaticEnvironmentReader : IMetadataV2EnvironmentSnapshotReader
    {
        private readonly Dictionary<string, MetadataV2GraphSnapshot> _snapshots;

        public StaticEnvironmentReader(params MetadataV2Graph[] graphs)
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

    private sealed class CancellationAwareReleasePackageStore : IMetadataReleasePackageStore
    {
        private readonly InMemoryMetadataReleasePackageStore _inner = new();

        public Task<MetadataReleasePackage> CreateAsync(
            MetadataReleasePackage package,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(package.Metadata.Name, "cancelled-package", StringComparison.Ordinal))
            {
                throw new OperationCanceledException("Simulated package store cancellation.");
            }

            return _inner.CreateAsync(package, cancellationToken);
        }

        public Task<MetadataReleasePackage?> GetAsync(
            Guid packageId,
            CancellationToken cancellationToken = default)
            => _inner.GetAsync(packageId, cancellationToken);
    }
}
