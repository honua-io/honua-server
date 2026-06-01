// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// End-to-end smoke test for the mobile offline demo fixture (honua-server#965).
/// Exercises createReplica → extractChanges → applyEdits → synchronizeReplica → query
/// against the seeded <c>mobile_offline_demo / 68910</c> layer that the mobile
/// Cloud Acceptance workflow targets. Prevents regressions of the FeatureServer
/// replication contract that unblocks honua-mobile#92.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class MobileOfflineDemoFixtureReplicationTests : IAsyncLifetime
{
    private const string ServiceId = "mobile_offline_demo";
    private const int OfflineSitesLayerId = 68910;
    private const int UpdateControlObjectId = 6891001;
    private static readonly string[] MobileOfflineCapabilities =
    [
        "Query", "Extract", "Create", "Update", "Delete", "Editing", "Sync"
    ];
    private static readonly string[] MobileOfflineSupportedFormats = ["JSON", "GeoJSON"];

    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        var schema = _fixture.CurrentSchema
            ?? throw new InvalidOperationException("WebAppFixture did not initialize an isolated schema.");

        var seedPath = RepositoryPaths.Resolve("tests", "seed", "mobile-offline-demo-v1.sql");
        var seedSql = await File.ReadAllTextAsync(seedPath);

        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = seedSql;
        await command.ExecuteNonQueryAsync();

        await PublishMobileOfflineMetadataV2Async();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private Task PublishMobileOfflineMetadataV2Async()
        => PublishMobileOfflineMetadataV2Async(includeAttributesAccessor: true);

    private async Task PublishMobileOfflineMetadataV2Async(bool includeAttributesAccessor)
    {
        var provider = _fixture.GetService<IMetadataV2GraphProvider>() as TestMetadataV2GraphProvider
            ?? throw new InvalidOperationException(
                "Test V2 graph provider not registered as TestMetadataV2GraphProvider.");
        var snapshot = await provider.GetCurrentAsync();
        var graph = snapshot.Graph;

        var accessPolicy = new AccessPolicy
        {
            AllowAnonymous = true,
            AllowAnonymousWrite = true
        };

        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "svc-mobile-offline-demo-feature",
                Name = ServiceId,
                Title = ServiceId,
                Description = "Deterministic SDK-backed mobile offline field operations fixture"
            },
            ServiceType = MetadataV2ServiceType.EsriFeatureService,
            Route = $"/rest/services/{ServiceId}/FeatureServer",
            Protocols = [MetadataV2ServiceProtocols.FeatureServer],
            AccessPolicy = accessPolicy,
            SpatialReference = MetadataV2SpatialReference.Wgs84,
            Options = new Dictionary<string, JsonElement>
            {
                ["capabilities"] = JsonSerializer.SerializeToElement(MobileOfflineCapabilities),
                ["supportedFormats"] = JsonSerializer.SerializeToElement(MobileOfflineSupportedFormats)
            }
        };

        var siteResource = CreateMobileOfflineResource(
            layerId: OfflineSitesLayerId,
            name: "Offline Field Sites",
            description: "Mobile-editable point layer for SDK offline create, update, delete, and conflict scenarios.",
            geometryType: MetadataV2GeometryType.Point,
            bbox: new MetadataV2Bbox { West = -158.1250, South = 21.2600, East = -157.8500, North = 21.4300 },
            displayField: "site_name",
            temporalField: "inspection_date",
            fields:
            [
                Field("objectid", MetadataV2FieldType.Integer, nullable: false, "Object ID", ["id.primary"]),
                Field("globalid", MetadataV2FieldType.String, nullable: false, "Stable mobile client/global identifier", length: 64),
                Field("site_name", MetadataV2FieldType.String, nullable: false, "Field site name", length: 128),
                Field("status", MetadataV2FieldType.String, nullable: false, "Workflow status", length: 32),
                Field("priority", MetadataV2FieldType.String, nullable: false, "Dispatch priority", length: 32),
                Field("assigned_to", MetadataV2FieldType.String, nullable: true, "Assigned mobile user", length: 64),
                Field("inspection_date", MetadataV2FieldType.DateTime, nullable: false, "Inspection timestamp"),
                Field("sync_version", MetadataV2FieldType.Integer, nullable: false, "Deterministic offline conflict token"),
                Field("offline_action", MetadataV2FieldType.String, nullable: false, "Fixture role for offline harness", length: 32),
                Field("notes", MetadataV2FieldType.String, nullable: true, "Operator notes", length: 1024),
                Field("shape", MetadataV2FieldType.Geometry, nullable: true, "Geometry", ["geometry.primary"])
            ],
            accessPolicy);

        var zoneResource = CreateMobileOfflineResource(
            layerId: 68920,
            name: "Offline Work Zones",
            description: "Polygon context layer included in the offline package as readonly operational context.",
            geometryType: MetadataV2GeometryType.Polygon,
            bbox: new MetadataV2Bbox { West = -158.1250, South = 21.2600, East = -157.7000, North = 21.5200 },
            displayField: "zone_name",
            temporalField: null,
            fields:
            [
                Field("objectid", MetadataV2FieldType.Integer, nullable: false, "Object ID", ["id.primary"]),
                Field("globalid", MetadataV2FieldType.String, nullable: false, "Stable zone identifier", length: 64),
                Field("zone_name", MetadataV2FieldType.String, nullable: false, "Work zone name", length: 128),
                Field("zone_status", MetadataV2FieldType.String, nullable: false, "Zone status", length: 32),
                Field("sync_version", MetadataV2FieldType.Integer, nullable: false, "Readonly context generation token"),
                Field("notes", MetadataV2FieldType.String, nullable: true, "Zone notes", length: 1024),
                Field("shape", MetadataV2FieldType.Geometry, nullable: true, "Geometry", ["geometry.primary"])
            ],
            accessPolicy);

        var storageBindings = new[]
        {
            CreateMobileOfflineStorageBinding(OfflineSitesLayerId, includeAttributesAccessor),
            CreateMobileOfflineStorageBinding(68920, includeAttributesAccessor)
        };
        var publications = new[]
        {
            CreateMobileOfflinePublication(OfflineSitesLayerId),
            CreateMobileOfflinePublication(68920)
        };

        provider.SetGraph(graph with
        {
            Revision = graph.Revision + 1,
            Services = graph.Services
                .Where(static existing => existing.Metadata.Id != "svc-mobile-offline-demo-feature")
                .Append(service)
                .ToArray(),
            Resources = graph.Resources
                .Where(static existing => existing.Metadata.Id is not ("res-layer-68910" or "res-layer-68920"))
                .Append(siteResource)
                .Append(zoneResource)
                .ToArray(),
            StorageBindings = graph.StorageBindings
                .Where(static existing => existing.Metadata.Id is not ("binding-layer-68910" or "binding-layer-68920"))
                .Concat(storageBindings)
                .ToArray(),
            Publications = graph.Publications
                .Where(static existing => existing.Metadata.Id is not ("pub-mobile-offline-feature-68910" or "pub-mobile-offline-feature-68920"))
                .Concat(publications)
                .ToArray()
        });
    }

    private static MetadataV2Resource CreateMobileOfflineResource(
        int layerId,
        string name,
        string description,
        MetadataV2GeometryType geometryType,
        MetadataV2Bbox bbox,
        string displayField,
        string? temporalField,
        IReadOnlyList<MetadataV2Field> fields,
        AccessPolicy accessPolicy)
    {
        var bindingId = $"binding-layer-{layerId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        return new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = $"res-layer-{layerId.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                Name = name,
                Title = name,
                Description = description
            },
            Type = MetadataV2ResourceType.FeatureDataset,
            StorageBindingIds = [bindingId],
            PrimaryStorageBindingId = bindingId,
            AccessPolicy = accessPolicy,
            SchemaFields = fields,
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = geometryType,
                Bbox = bbox,
                PrimaryGeometryField = "shape"
            },
            Temporal = temporalField is null
                ? null
                : new MetadataV2ResourceTemporal { StartTimeField = temporalField },
            Display = new MetadataV2ResourceDisplay { DisplayField = displayField },
            Editing = new MetadataV2ResourceEditing { CanModify = true },
        };
    }

    private static MetadataV2StorageBinding CreateMobileOfflineStorageBinding(
        int layerId,
        bool includeAttributesAccessor = true)
    {
        // The seed's shared 'features' table holds rows for every test layer keyed by a
        // 'layer_id' discriminator; the geometry lives in the 'geometry' column and the
        // mobile-offline schema fields are persisted in the 'attributes' JSONB column.
        // FeatureStorageMapping.ParseRelationalLocator rejects any locator that
        // contains ':' so use the table name directly and expose the layer/geometry/
        // attributes columns via Options (matching WebAppFixtureMetadataV2Mixin.BuildDefaultTestGraph).
        var options = new Dictionary<string, JsonElement>
        {
            ["geometryColumn"] = JsonSerializer.SerializeToElement("geometry"),
            // Shared 'features' table => declare the layer discriminator so the reader
            // constrains reads to this binding's StorageLayerId (cross-layer isolation,
            // honua-server#1238 follow-up).
            ["layerDiscriminatorColumn"] = JsonSerializer.SerializeToElement("layer_id")
        };

        // When omitted (includeAttributesAccessor: false) the binding reproduces the
        // honua-server#1238 drift: the seed left storage_options empty so the reader
        // projects bare columns ('globalid', ...) against the shared features table,
        // which has no such columns => Postgres 42703. The seed/bridge fix supplies
        // this accessor so declared fields resolve as attributes->>'field'.
        if (includeAttributesAccessor)
        {
            options["attributesColumn"] = JsonSerializer.SerializeToElement("attributes");
        }

        return new()
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = $"binding-layer-{layerId.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                Name = $"binding-layer-{layerId.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            },
            ResourceId = $"res-layer-{layerId.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = "features",
            StorageLayerId = layerId,
            Options = options,
            Capabilities =
            [
                MetadataV2StorageBindingCapability.Query,
                MetadataV2StorageBindingCapability.Filter,
                MetadataV2StorageBindingCapability.Sort,
                MetadataV2StorageBindingCapability.Aggregate,
                MetadataV2StorageBindingCapability.Edit,
                MetadataV2StorageBindingCapability.Transactions
            ]
        };
    }

    private static MetadataV2Publication CreateMobileOfflinePublication(int layerId)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = $"pub-mobile-offline-feature-{layerId.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                Name = layerId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            ServiceId = "svc-mobile-offline-demo-feature",
            ResourceId = $"res-layer-{layerId.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            StorageBindingId = $"binding-layer-{layerId.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            Identifier = new MetadataV2PublicationIdentifier
            {
                Value = layerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                IsNumeric = true
            },
            PublicationType = MetadataV2PublicationType.EsriFeatureLayer
        };

    private static MetadataV2Field Field(
        string name,
        MetadataV2FieldType type,
        bool nullable,
        string description,
        IReadOnlyList<string>? semanticRoles = null,
        int? length = null)
        => new()
        {
            Name = name,
            Type = type,
            Nullable = nullable,
            Description = description,
            SemanticRoles = semanticRoles ?? [],
            Length = length
        };

    [IntegrationTest]
    [Operation(Operations.CreateReplica)]
    [Endpoint("POST /rest/services/mobile_offline_demo/FeatureServer/createReplica")]
    public async Task MobileOfflineFixture_SupportsCloudAcceptanceReplicationLifecycle()
    {
        var replicaId = await CreateReplicaAsync();
        await ExtractInitialChangesAsync(replicaId);
        await ApplyOfflineEditAsync();
        await SynchronizeReplicaAsync(replicaId);
        await QueryOfflineSitesAsync();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/mobile_offline_demo/FeatureServer/68910/query")]
    public async Task MobileOfflineFixture_QueryReturnsSeededFeatures()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{ServiceId}/FeatureServer/{OfflineSitesLayerId}/query"
            + "?where=1%3D1&outFields=*&returnGeometry=true&f=json");

        response.Be200Ok();

        using var document = await ReadJsonDocumentAsync(response);
        var root = document.RootElement;
        root.TryGetProperty("error", out _).Should().BeFalse(
            "the seeded layer should respond with a non-error GeoServices payload");

        var features = root.GetProperty("features");
        features.GetArrayLength().Should().BeGreaterThanOrEqualTo(3,
            "the v1 seed inserts three deterministic offline-site features");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/mobile_offline_demo/FeatureServer")]
    public async Task MobileOfflineFixture_ServiceMetadata_AdvertisesSyncEnabled()
    {
        // The fixture declares a "Sync" capability, so the FeatureServer service
        // metadata must advertise syncEnabled=true (the replica/extractChanges
        // surface Esri offline clients discover). Without the Sync capability the
        // flag stays false.
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{ServiceId}/FeatureServer?f=json");

        response.Be200Ok();

        using var document = await ReadJsonDocumentAsync(response);
        var root = document.RootElement;
        root.TryGetProperty("error", out _).Should().BeFalse();
        root.GetProperty("syncEnabled").GetBoolean().Should().BeTrue(
            "the fixture declares a 'Sync' capability so the service is sync-enabled");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/mobile_offline_demo/FeatureServer/68910")]
    public async Task MobileOfflineFixture_LayerMetadata_AdvertisesSupportsTopFeaturesQuery()
    {
        // queryTopFeatures is served unconditionally, so advanced-query layers must
        // advertise advancedQueryCapabilities.supportsTopFeaturesQuery=true.
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{ServiceId}/FeatureServer/{OfflineSitesLayerId}?f=json");

        response.Be200Ok();

        using var document = await ReadJsonDocumentAsync(response);
        var root = document.RootElement;
        root.TryGetProperty("error", out _).Should().BeFalse();
        root.GetProperty("advancedQueryCapabilities")
            .GetProperty("supportsTopFeaturesQuery").GetBoolean().Should().BeTrue(
            "queryTopFeatures is served unconditionally for advanced-query layers");
    }

    // Regression for honua-server#1238: the JSONB-attribute layer must be queryable
    // ONLY because the binding declares attributesColumn=attributes. This test republishes
    // the graph WITHOUT that accessor to prove the bug reproduces (bare columns =>
    // Postgres 42703 => GeoServices 400 "Invalid query syntax."), then republishes WITH
    // the accessor (what the seed/bridge fix now supplies) to prove 200 with features.
    // This guards against the TestKit attributesColumn shortcut silently masking the drift.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/mobile_offline_demo/FeatureServer/68910/query")]
    public async Task MobileOfflineFixture_JsonbAttributes_RequireStorageAccessorForQuery()
    {
        var requestUri =
            $"/rest/services/{ServiceId}/FeatureServer/{OfflineSitesLayerId}/query"
            + "?where=1%3D1&outFields=*&returnGeometry=true&f=json";

        // Reproduce the drift: no attributesColumn => bare-column projection => 42703.
        await PublishMobileOfflineMetadataV2Async(includeAttributesAccessor: false);

        var brokenResponse = await _fixture.Client.GetAsync(requestUri);
        using (var brokenDocument = await ReadJsonDocumentAsync(brokenResponse))
        {
            brokenDocument.RootElement.TryGetProperty("error", out _).Should().BeTrue(
                "without the JSONB accessor the reader projects bare columns and Postgres "
                + "rejects the query with 42703 (the honua-server#1238 failure)");
        }

        // The fix: with the accessor, declared fields resolve as attributes->>'field'.
        await PublishMobileOfflineMetadataV2Async(includeAttributesAccessor: true);

        var fixedResponse = await _fixture.Client.GetAsync(requestUri);
        fixedResponse.Be200Ok();

        using var fixedDocument = await ReadJsonDocumentAsync(fixedResponse);
        var root = fixedDocument.RootElement;
        root.TryGetProperty("error", out _).Should().BeFalse(
            "with attributesColumn=attributes the JSONB-backed layer is queryable");
        root.GetProperty("features").GetArrayLength().Should().BeGreaterThanOrEqualTo(3);
    }

    private async Task<string> CreateReplicaAsync()
    {
        var payload = JsonSerializer.Serialize(new
        {
            replicaName = "CloudAcceptanceSmoke",
            layers = OfflineSitesLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            syncModel = "perReplica",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{ServiceId}/FeatureServer/createReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "createReplica must succeed against the seeded mobile offline service");

        using var document = await ReadJsonDocumentAsync(response);
        var root = document.RootElement;
        root.TryGetProperty("error", out _).Should().BeFalse();

        var replicaId = root.GetProperty("replicaID").GetString();
        replicaId.Should().NotBeNullOrWhiteSpace("createReplica must return a usable replicaID");

        root.GetProperty("layers")
            .EnumerateArray()
            .Any(layer => layer.GetProperty("id").GetInt32() == OfflineSitesLayerId)
            .Should().BeTrue("the created replica must include layer 68910");

        return replicaId!;
    }

    private async Task ExtractInitialChangesAsync(string replicaId)
    {
        var payload = JsonSerializer.Serialize(new
        {
            replicaID = replicaId,
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{ServiceId}/FeatureServer/extractChanges",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "extractChanges must succeed against the seeded mobile offline replica");

        using var document = await ReadJsonDocumentAsync(response);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("replicaID").GetString().Should().Be(replicaId);
        root.GetProperty("layerChanges").GetArrayLength().Should().BeGreaterThanOrEqualTo(1,
            "extractChanges must return a layer-changes envelope for the seeded layer");
    }

    private async Task ApplyOfflineEditAsync()
    {
        var editsRequest = new ApplyEditsRequest
        {
            Updates = new[]
            {
                new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["objectid"] = UpdateControlObjectId,
                        ["status"] = "in_progress",
                        ["notes"] = "Mobile-edit applied during cloud acceptance smoke test."
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(editsRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{ServiceId}/FeatureServer/{OfflineSitesLayerId}/applyEdits",
            content);

        response.Be200Ok();

        var body = await response.Content.ReadAsStringAsync();
        var applyResponse = JsonSerializer.Deserialize(
            body, FeatureServerJsonContext.Default.ApplyEditsResponse);
        applyResponse.Should().NotBeNull();
        applyResponse!.Success.Should().BeTrue("the seeded fixture must accept the offline-control update");

        applyResponse.UpdateResults.Should().NotBeNullOrEmpty(
            "applyEdits must return per-edit results for the seeded update target");
        applyResponse.UpdateResults![0].Success.Should().BeTrue(
            "the mobile-edit update against the seeded objectid must succeed");
    }

    private async Task SynchronizeReplicaAsync(string replicaId)
    {
        var payload = JsonSerializer.Serialize(new
        {
            replicaID = replicaId,
            syncDirection = "download",
            f = "json"
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{ServiceId}/FeatureServer/synchronizeReplica",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "synchronizeReplica must succeed against the seeded mobile offline replica");

        using var document = await ReadJsonDocumentAsync(response);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("replicaID").GetString().Should().Be(replicaId);
    }

    private async Task QueryOfflineSitesAsync()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{ServiceId}/FeatureServer/{OfflineSitesLayerId}/query"
            + "?where=offline_action%20%3D%20%27update-control%27&outFields=objectid,status,notes&returnGeometry=false&f=json");

        response.Be200Ok();

        using var document = await ReadJsonDocumentAsync(response);
        var root = document.RootElement;
        root.TryGetProperty("error", out _).Should().BeFalse();

        var features = root.GetProperty("features");
        features.GetArrayLength().Should().Be(1,
            "the offline-control feature must be queryable after applyEdits");

        var attributes = features[0].GetProperty("attributes");
        attributes.GetProperty("status").GetString().Should().Be("in_progress",
            "query must surface the persisted post-applyEdits state");
    }

    private static async Task<JsonDocument> ReadJsonDocumentAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(content);
    }
}
