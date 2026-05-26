// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Queries.Filters;
using Honua.TestKit.Infrastructure;
using Honua.TestKit.Seeding;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;
using CoreSslMode = Honua.Core.Features.Security.Domain.SslMode;

namespace Honua.TestKit;

/// <summary>
/// Web application fixture for integration tests.
/// Combines WebApplicationFactory with PostgresFixture.
/// Supports service replacement and schema-based isolation.
/// </summary>
public sealed class WebAppFixture : IAsyncLifetime
{
    private static readonly SemaphoreSlim _sharedLock = new(1, 1);
    private static readonly SemaphoreSlim _secureConnectionLock = new(1, 1);
    private static WebApplicationFactory<Program>? _sharedFactory;
    private static PostgresFixture? _sharedPostgres;
    private static int _sharedRefCount;
    private static bool _sharedInitialized;

    private PostgresFixture? _postgres;
    private readonly List<Action<IServiceCollection>> _serviceConfigurations = [];
    private Action<IWebHostBuilder>? _configureWebHost;
    private WebApplicationFactory<Program>? _factory;
    private string? _currentSchema;
    private bool _useSharedServer;
    private string? _seedPath;
    private string? _seedProfile;
    private IServiceScope? _serviceScope;

    /// <summary>
    /// Test service ID used for testing operations.
    /// </summary>
    public const string TestServiceId = "test";

    /// <summary>
    /// Test layer ID used for testing operations.
    /// </summary>
    public const int TestLayerId = 0;

    private const string StableTestGeocodingBaseUrl = "https://8.8.8.8/nominatim";
    private const string TestEncryptionMasterKey = "test-master-key-that-is-at-least-32-characters-long-for-security";
    private const string TestEncryptionSalt = "dGVzdC1zYWx0LWZvci1lbmNyeXB0aW9uLXRlc3RpbmctcHVycG9zZXM=";
    private const string TestSecureConnectionName = "test";
    private const string TestSecureConnectionCreatedBy = "test-fixture";

    /// <summary>
    /// Admin password used by <see cref="CreateAdminClient"/> and configured into the
    /// shared test server's <c>HONUA_ADMIN_PASSWORD</c> setting. Exposed so tests that
    /// build their own non-shared <see cref="WebAppFixture"/> (e.g. cross-client
    /// certification fixtures) can wire the same value into their custom web host
    /// configuration without re-declaring a string literal that must stay in lockstep.
    /// </summary>
    public const string SharedAdminPassword = "test-admin-password";
    private static readonly TimeSpan _defaultTestClientTimeout = TimeSpan.FromMinutes(5);

    public WebAppFixture()
    {
    }

    public HttpClient Client { get; private set; } = null!;

    public PostgresFixture Postgres => _useSharedServer
        ? _sharedPostgres ?? throw new InvalidOperationException("Shared Postgres fixture not initialized.")
        : _postgres ?? throw new InvalidOperationException("Postgres fixture not initialized.");

    public PostgresFixture PostgresFixture => Postgres;

    public string? CurrentSchema => _currentSchema;

    /// <summary>
    /// Gets the database connection provider for test scenarios.
    /// </summary>
    public IDatabaseConnectionProvider DatabaseConnectionProvider => GetService<IDatabaseConnectionProvider>();

    /// <summary>
    /// Gets the service provider from the test server's DI container.
    /// </summary>
    public IServiceProvider Services => (_useSharedServer ? _sharedFactory : _factory)?.Services
        ?? throw new InvalidOperationException("Web application factory not initialized.");

    private bool HasCustomConfiguration => _serviceConfigurations.Count > 0 || _configureWebHost != null;

    public async Task InitializeAsync()
    {
        _useSharedServer = !HasCustomConfiguration;

        if (_useSharedServer)
        {
            await InitializeSharedAsync();
            return;
        }

        _postgres = new PostgresFixture();
        await _postgres.InitializeAsync();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // Configure test environment
                builder.UseEnvironment("Test");

                // Configure authentication bypass for test environment.
                // BOTH flags are required (see honua-server#1144): HONUA_DEV_AUTH
                // alone is insufficient outside of Test+explicit-ack to prevent
                // accidental bypass activation in Staging/QA.
                builder.UseSetting("HONUA_DEV_AUTH", "true");
                builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "true");
                builder.UseSetting("HONUA_SKIP_MIGRATIONS", "true");

                _configureWebHost?.Invoke(builder);

                // Configure application configuration with test connection string BEFORE app startup
                builder.ConfigureAppConfiguration((context, configBuilder) =>
                {
                    var attachmentsPath = Path.Combine(Directory.GetCurrentDirectory(), "tmp", "attachments");
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:honua"] = _postgres.ConnectionString,
                        ["ConnectionStrings:DefaultConnection"] = _postgres.ConnectionString,
                        // Avoid live DNS dependency during startup validation in integration tests.
                        ["Geocoding:Nominatim:BaseUrl"] = StableTestGeocodingBaseUrl,
                        ["Geocoding:Providers:Nominatim:BaseUrl"] = StableTestGeocodingBaseUrl,
                        ["HONUA_SKIP_MIGRATIONS"] = "true",
                        ["Limits:Connections:RequestTimeout"] = "00:05:00",
                        ["Limits:Query:QueryTimeout"] = "00:02:00",
                        ["FileStorage:Provider"] = "Local",
                        ["FileStorage:LocalStorage:BasePath"] = attachmentsPath,
                        ["Security:ConnectionEncryption:MasterKey"] = TestEncryptionMasterKey,
                        ["Security:ConnectionEncryption:Salt"] = TestEncryptionSalt
                    });
                });

                builder.ConfigureTestServices(services =>
                {
                    // Remove and re-register all PostgreSQL services with test connection string
                    services.RemoveAll<NpgsqlDataSource>();
                    services.RemoveAll<IFeatureReader>();
                    services.RemoveAll<IFeatureWriter>();
                    services.RemoveAll<ITileProvider>();
                    services.RemoveAll<IRelationshipStore>();
                    services.RemoveAll<IStreamingFeatureStore>();
                    services.RemoveAll<IAttachmentStore>();
                    services.RemoveAll<ILayerCatalog>();
                    services.RemoveAll<ITableDiscoveryService>();
                    services.RemoveAll<IDatabaseHealthChecker>();
                    services.RemoveAll<IDatabaseConnectionProvider>();
                    services.RemoveAll<ICrsDetectionService>();
                    services.RemoveAll<IFileImportService>();
                    services.RemoveAll<ISqlFilterTranslator>();

                    // Create test configuration with connection string
                    var testConfiguration = new ConfigurationBuilder()
                        .AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["ConnectionStrings:DefaultConnection"] = _postgres.ConnectionString,
                            ["Limits:Connections:RequestTimeout"] = "00:05:00",
                            ["Limits:Query:QueryTimeout"] = "00:02:00",
                            ["Security:ConnectionEncryption:MasterKey"] = TestEncryptionMasterKey,
                            ["Security:ConnectionEncryption:Salt"] = TestEncryptionSalt
                        })
                        .Build();

                    // Register all PostgreSQL services using the Postgres layer's extension method
                    // This ensures proper dependency injection without Server/TestKit directly instantiating Postgres types
                    Honua.Postgres.ServiceCollectionExtensions.AddPostgreSqlServices(services, testConfiguration);

                    // Override the data source to avoid multiplexing so schema-based tests keep session state.
                    services.RemoveAll<NpgsqlDataSource>();
                    services.AddSingleton<NpgsqlDataSource>(_ =>
                    {
                        var dataSourceBuilder = new NpgsqlDataSourceBuilder(_postgres.ConnectionString);
                        dataSourceBuilder.ConnectionStringBuilder.Multiplexing = false;
                        return dataSourceBuilder.Build();
                    });

                    // Override specific services for testing
                    services.RemoveAll<ILayerCatalog>();
                    services.AddScoped<ILayerCatalog, Honua.Postgres.Features.Catalog.PostgresLayerCatalog>();

                    // Replace the Postgres-backed Metadata v2 provider with an in-memory fixture
                    // so endpoints that have migrated off ILayerCatalog can read a snapshot without
                    // a migrated snapshot row being present in the test database.
                    RegisterDefaultMetadataV2Graph(services);

                    // Override database connection provider with test-specific implementation
                    services.RemoveAll<IDatabaseConnectionProvider>();
                    services.AddScoped<IDatabaseConnectionProvider>(serviceProvider =>
                    {
                        var dataSource = serviceProvider.GetRequiredService<NpgsqlDataSource>();
                        return new TestDatabaseConnectionProvider(dataSource, () => _currentSchema);
                    });

                    // Apply custom service configurations
                    foreach (var configure in _serviceConfigurations)
                    {
                        configure(services);
                    }
                });
            });

        Client = CreateClient();
        _serviceScope = _factory.Services.CreateScope();

        if (string.IsNullOrWhiteSpace(_currentSchema))
        {
            _currentSchema = await _postgres.CreateIsolatedSchemaAsync(nameof(WebAppFixture));
            await SeedSchemaAsync(_currentSchema);
        }

        await EnsureTestSecureConnectionAsync();
    }

    /// <summary>
    /// Ensures a large test dataset exists for streaming performance tests
    /// </summary>
    public async Task EnsureLargeTestDatasetAsync()
    {
        if (string.IsNullOrWhiteSpace(_currentSchema))
        {
            throw new InvalidOperationException("Test schema not initialized.");
        }

        await using var connection = await Postgres.GetConnectionAsync(_currentSchema);

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM features WHERE layer_id = @layerId;";
        countCommand.Parameters.Add(new NpgsqlParameter { ParameterName = "@layerId", Value = TestLayerId });
        var existingCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

        if (existingCount >= 2000)
        {
            return;
        }

        var additionalFeaturesNeeded = 2000 - existingCount;

        await using var transaction = await connection.BeginTransactionAsync();
        await using var insertCommand = connection.CreateCommand();
        insertCommand.Transaction = transaction;
        insertCommand.CommandText = """
            INSERT INTO features (layer_id, geometry, attributes)
            VALUES (@layerId, ST_GeomFromWKB(@geometry, 4326), @attributes::jsonb);
            """;
        insertCommand.Parameters.Add(new NpgsqlParameter { ParameterName = "@layerId", Value = TestLayerId });
        var geometryParam = new NpgsqlParameter { ParameterName = "@geometry", Value = DBNull.Value };
        var attributesParam = new NpgsqlParameter { ParameterName = "@attributes", Value = DBNull.Value };
        insertCommand.Parameters.Add(geometryParam);
        insertCommand.Parameters.Add(attributesParam);

        for (var i = 0; i < additionalFeaturesNeeded; i++)
        {
            geometryParam.Value = CreateTestPointGeometry(-180 + (i % 360), -90 + ((i / 360) % 180));

            var attributes = new Dictionary<string, object?>
            {
                ["Name"] = $"Streaming Test Feature {i}",
                ["Category"] = "Performance Test",
                ["Value"] = i % 100,
                ["TestBatch"] = "LargeDataset"
            };
            attributesParam.Value = JsonSerializer.Serialize(attributes);

            await insertCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    /// <summary>
    /// Builds the default Metadata v2 graph snapshot registered into the test DI container so
    /// that endpoints which have migrated off <see cref="ILayerCatalog"/> have something to read
    /// from. Mirrors the layer ids seeded by <c>tests/seed/server.yaml</c> so existence probes
    /// keyed on the v1 layer id continue to find a matching publication. Tests that need a richer
    /// graph can replace the registration through <see cref="ConfigureServices"/>.
    /// </summary>
    private static MetadataV2Graph BuildDefaultTestGraph()
    {
        // The Postgres test seed (tests/seed/server.yaml) registers a single "test" service
        // with every protocol enabled. Mirror that on the V2 graph so capability gates
        // (ServiceProtocols.IsProtocolEnabled on the OGC API family) match the v1 behaviour.
        var allProtocols = Honua.Core.Features.Catalog.Domain.ServiceProtocols.All;
        var builder = new Honua.TestKit.Infrastructure.TestMetadataV2GraphBuilder()
            .AddService(
                "svc-test",
                "test",
                route: "/ogc/features",
                protocols: allProtocols,
                description: "Default OGC API test service");

        // Cover the layer ids inserted by server.yaml (0..2 and the spatial-reference fixtures
        // at 101..104). Any test that needs a different id range can extend or replace the graph.
        int[] seededLayerIndices = [0, 1, 2, 3, 4, 5, 101, 102, 103, 104];
        foreach (var layerIndex in seededLayerIndices)
        {
            var resourceId = $"res-layer-{layerIndex}";
            builder
                .AddResource(
                    resourceId,
                    $"layer-{layerIndex}",
                    MetadataV2ResourceType.FeatureDataset,
                    fields: GetSeededLayerSchemaFields(layerIndex),
                    spatial: GetSeededLayerSpatial(layerIndex))
                .AddStorageBinding(
                    id: $"storage-layer-{layerIndex}",
                    resourceId: resourceId,
                    locator: $"features:{layerIndex}",
                    storageLayerId: layerIndex)
                .AddPublication(
                    id: $"pub-layer-{layerIndex}",
                    serviceId: "svc-test",
                    resourceId: resourceId,
                    layerIndex: layerIndex,
                    serviceLocalId: layerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // ImageServer publications for the canonical raster test layer (TestServiceId/TestLayerId).
        // ImageServer handler tests resolve their layer index against this snapshot.
        builder
            .AddResource("res-image-test", "test-layer", MetadataV2ResourceType.RasterDataset)
            .AddService("svc-image-test", TestServiceId, protocols: [ServiceProtocols.ImageServer], description: "Default ImageServer test service")
            .AddPublication(
                id: "pub-image-test",
                serviceId: "svc-image-test",
                resourceId: "res-image-test",
                layerIndex: TestLayerId,
                serviceLocalId: "test-layer",
                publicationType: MetadataV2PublicationType.EsriImageLayer);

        // FeatureServer and MapServer publications for the canonical "test" service.
        // The GeoServices REST catalog endpoint enumerates these directly from the V2
        // graph, so the directory only emits FeatureServer/MapServer entries when matching
        // EsriFeatureService / EsriMapService publications exist. We publish the same
        // layer ids that server.yaml seeds so /rest/services has services to return and
        // downstream FeatureServer/MapServer handler ports can resolve them by layer id.
        builder
            .AddService("svc-test-feature", "test", protocols: [ServiceProtocols.FeatureServer], description: "Default FeatureServer test service")
            .AddService("svc-test-map", "test", protocols: [ServiceProtocols.MapServer], description: "Default MapServer test service")
            .AddService("svc-test-stac", "test", route: "/stac", protocols: [ServiceProtocols.Stac], description: "Default STAC test service");
        foreach (var layerIndex in seededLayerIndices)
        {
            var resourceId = $"res-layer-{layerIndex}";
            builder
                .AddPublication(
                    id: $"pub-feature-{layerIndex}",
                    serviceId: "svc-test-feature",
                    resourceId: resourceId,
                    layerIndex: layerIndex,
                    serviceLocalId: layerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    publicationType: MetadataV2PublicationType.EsriFeatureLayer)
                .AddPublication(
                    id: $"pub-map-{layerIndex}",
                    serviceId: "svc-test-map",
                    resourceId: resourceId,
                    layerIndex: layerIndex,
                    serviceLocalId: layerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    publicationType: MetadataV2PublicationType.EsriMapLayer)
                .AddPublication(
                    id: $"pub-stac-{layerIndex}",
                    serviceId: "svc-test-stac",
                    resourceId: resourceId,
                    layerIndex: layerIndex,
                    serviceLocalId: layerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    publicationType: MetadataV2PublicationType.StacCollection);
        }

        return builder.Build();
    }

    private static void RegisterDefaultMetadataV2Graph(IServiceCollection services)
    {
        services.RemoveAll<IMetadataV2GraphProvider>();
        services.AddSingleton<IMetadataV2GraphProvider>(_ =>
            new Honua.TestKit.Infrastructure.TestMetadataV2GraphProvider(BuildDefaultTestGraph()));
    }

    /// <summary>
    /// Returns the V2 schema fields for the layers the Postgres test seed
    /// (tests/seed/server.yaml) populates via the v1 layer_fields table. OGC API
    /// Features queryables, OData $metadata, STAC properties, and other
    /// schema-driven endpoints all read this from the V2 resource graph after the
    /// metadata-v2 cutover. Layers without seeded fields (everything but 0/1/2)
    /// return an empty list — matching the v1 fixture behavior where those layers
    /// have no layer_fields rows.
    /// </summary>
    private static IEnumerable<MetadataV2Field>? GetSeededLayerSchemaFields(int layerIndex)
        => layerIndex switch
        {
            0 =>
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false, Description = "Object ID" },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = true, Description = "Name" },
                new MetadataV2Field { Name = "description", Type = MetadataV2FieldType.String, Nullable = true, Description = "Description" },
                new MetadataV2Field { Name = "category", Type = MetadataV2FieldType.String, Nullable = true, Description = "Category" },
                new MetadataV2Field { Name = "timestamp", Type = MetadataV2FieldType.DateTime, Nullable = true, Description = "Timestamp" },
                new MetadataV2Field { Name = "event_date", Type = MetadataV2FieldType.DateTime, Nullable = true, Description = "Event date" },
                new MetadataV2Field { Name = "created_date", Type = MetadataV2FieldType.Date, Nullable = true, Description = "Created date" },
                new MetadataV2Field
                {
                    Name = "shape",
                    Type = MetadataV2FieldType.Geometry,
                    Nullable = true,
                    Description = "Geometry",
                    SemanticRoles = ["geometry.primary"],
                },
            ],
            1 =>
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false, Description = "Object ID" },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = true, Description = "Name field" },
                new MetadataV2Field { Name = "related_id", Type = MetadataV2FieldType.Integer, Nullable = true, Description = "Foreign key to origin layer" },
                new MetadataV2Field { Name = "description", Type = MetadataV2FieldType.String, Nullable = true, Description = "Description" },
                new MetadataV2Field { Name = "category", Type = MetadataV2FieldType.String, Nullable = true, Description = "Category" },
                new MetadataV2Field
                {
                    Name = "shape",
                    Type = MetadataV2FieldType.Geometry,
                    Nullable = true,
                    Description = "Geometry",
                    SemanticRoles = ["geometry.primary"],
                },
            ],
            2 =>
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false, Description = "Object ID" },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = true, Description = "Name field" },
                new MetadataV2Field { Name = "secondary_id", Type = MetadataV2FieldType.Integer, Nullable = true, Description = "Foreign key to origin layer" },
                new MetadataV2Field { Name = "type", Type = MetadataV2FieldType.String, Nullable = true, Description = "Type field" },
                new MetadataV2Field
                {
                    Name = "shape",
                    Type = MetadataV2FieldType.Geometry,
                    Nullable = true,
                    Description = "Geometry",
                    SemanticRoles = ["geometry.primary"],
                },
            ],
            _ => null,
        };

    private static MetadataV2ResourceSpatial? GetSeededLayerSpatial(int layerIndex)
    {
        if (GetSeededLayerSchemaFields(layerIndex)?.Any(static field => field.Type == MetadataV2FieldType.Geometry) != true)
        {
            return null;
        }

        return new MetadataV2ResourceSpatial
        {
            SpatialReference = MetadataV2SpatialReference.Wgs84,
            GeometryType = MetadataV2GeometryType.Point,
            Bbox = new MetadataV2Bbox
            {
                West = -180,
                South = -90,
                East = 180,
                North = 90
            },
            PrimaryGeometryField = "shape"
        };
    }

    /// <summary>
    /// During the v1→v2 cutover several protocol handlers (WMS/WMTS GetCapabilities,
    /// FeatureServer temporal extent, etc.) still read TimeInfo / AccessPolicy from the
    /// v1 Postgres catalog instead of the V2 graph. The Postgres test seed pre-populates
    /// honua.layers.metadata with a non-empty TimeInfo for the canonical test layer, so
    /// tests that need to clear or change those slots must also update the v1 jsonb
    /// column until those handlers migrate. This helper bridges that write to the
    /// shared honua.layers row (Database:Schema defaults to "honua" in test config
    /// and the seed seeds the shared honua schema, so all tests already read/write
    /// the same row — preserving the v1 coupling intentionally during cutover).
    /// </summary>
    private void WriteV1LayerCatalogBridge(int layerId, CatalogMetadata? metadata)
    {
        var json = metadata is null
            ? "null"
            : System.Text.Json.JsonSerializer.Serialize(
                metadata,
                Honua.Core.Features.Catalog.Domain.CatalogJsonContext.Default.CatalogMetadata);

        using var connection = Postgres.GetConnectionAsync().GetAwaiter().GetResult();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE honua.layers SET metadata = @metadata::jsonb WHERE layer_id = @layerId";
        var metadataParam = command.CreateParameter();
        metadataParam.ParameterName = "@metadata";
        metadataParam.Value = (object?)json ?? DBNull.Value;
        command.Parameters.Add(metadataParam);
        var idParam = command.CreateParameter();
        idParam.ParameterName = "@layerId";
        idParam.Value = layerId;
        command.Parameters.Add(idParam);
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// Bridging counterpart to <see cref="WriteV1LayerCatalogBridge"/> for service-level
    /// CatalogMetadata (EnabledProtocols, AccessPolicy). Same rationale: WMS/WMTS
    /// GetCapabilities, FeatureServer protocol gating, etc. still consult
    /// ServiceDefinition.Metadata from Postgres until those handlers move to V2.
    /// </summary>
    private void WriteV1ServiceCatalogBridge(string serviceName, CatalogMetadata? metadata)
    {
        var json = metadata is null
            ? "null"
            : System.Text.Json.JsonSerializer.Serialize(
                metadata,
                Honua.Core.Features.Catalog.Domain.CatalogJsonContext.Default.CatalogMetadata);

        using var connection = Postgres.GetConnectionAsync().GetAwaiter().GetResult();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE honua.services SET metadata = @metadata::jsonb, updated_at = NOW() WHERE LOWER(service_name) = LOWER(@serviceName)";
        var metadataParam = command.CreateParameter();
        metadataParam.ParameterName = "@metadata";
        metadataParam.Value = (object?)json ?? DBNull.Value;
        command.Parameters.Add(metadataParam);
        var nameParam = command.CreateParameter();
        nameParam.ParameterName = "@serviceName";
        nameParam.Value = serviceName;
        command.Parameters.Add(nameParam);
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// V2-aware helper that mirrors the v1 <c>ILayerMetadataUpdater</c> seed surface used
    /// by classic-protocol and STAC tests. Looks up the resource bound to the publication
    /// with <paramref name="layerIndex"/> and applies the supplied access policy, temporal
    /// metadata, spatial metadata, permanent filter, and/or STAC extension payload.
    /// Passing a value writes it; omitting an argument leaves that slot untouched. The
    /// <c>clear*</c> flags explicitly clear the corresponding slot (mirroring the v1
    /// "PUT empty CatalogMetadata" semantics).
    /// </summary>
    public void UpdateV2ResourceMetadata(
        int layerIndex,
        AccessPolicy? accessPolicy = null,
        MetadataV2ResourceTemporal? temporal = null,
        MetadataV2ResourceSpatial? spatial = null,
        MetadataV2PermanentFilter? permanentFilter = null,
        LayerExtrusionInfo? extrusion = null,
        JsonElement? stacExtension = null,
        bool clearAccessPolicy = false,
        bool clearTemporal = false,
        bool clearPermanentFilter = false,
        bool clearExtrusion = false)
    {
        var provider = GetService<IMetadataV2GraphProvider>() as Honua.TestKit.Infrastructure.TestMetadataV2GraphProvider
            ?? throw new InvalidOperationException(
                "Test V2 graph provider not registered as TestMetadataV2GraphProvider.");

        var snapshot = provider.GetCurrentAsync().AsTask().GetAwaiter().GetResult();
        var pub = snapshot.Graph.Publications.FirstOrDefault(p => p.LayerIndex == layerIndex);
        if (pub is null)
        {
            throw new InvalidOperationException(
                $"No publication for layer {layerIndex} in the test V2 graph.");
        }

        var resources = snapshot.Graph.Resources.ToArray();
        var mutated = false;
        for (var i = 0; i < resources.Length; i++)
        {
            if (!string.Equals(resources[i].Metadata.Id, pub.ResourceId, StringComparison.Ordinal))
            {
                continue;
            }

            var next = resources[i];
            if (clearAccessPolicy)
            {
                next = next with { AccessPolicy = null };
            }
            else if (accessPolicy is not null)
            {
                next = next with { AccessPolicy = accessPolicy };
            }

            if (clearTemporal)
            {
                next = next with { Temporal = null };
            }
            else if (temporal is not null)
            {
                next = next with { Temporal = temporal };
            }

            if (spatial is not null)
            {
                next = next with { Spatial = spatial };
            }

            if (clearPermanentFilter)
            {
                next = next with { PermanentFilter = null };
            }
            else if (permanentFilter is not null)
            {
                next = next with { PermanentFilter = permanentFilter };
            }

            if (stacExtension.HasValue)
            {
                var extensions = new Dictionary<string, JsonElement>(next.Extensions, StringComparer.Ordinal)
                {
                    ["stac"] = stacExtension.Value,
                };
                next = next with { Extensions = extensions };
            }

            if (clearExtrusion || extrusion is not null)
            {
                var extensions = new Dictionary<string, JsonElement>(next.Extensions, StringComparer.Ordinal);
                if (clearExtrusion)
                {
                    extensions.Remove("geoservices:extrusion");
                    extensions.Remove("extrusion");
                }
                else
                {
                    extensions["geoservices:extrusion"] = JsonSerializer.SerializeToElement(
                        extrusion,
                        CatalogJsonContext.Default.LayerExtrusionInfo);
                }

                next = next with { Extensions = extensions };
            }

            resources[i] = next;
            mutated = true;
        }

        if (!mutated)
        {
            return;
        }

        var updatedGraph = snapshot.Graph with
        {
            Resources = resources,
            Revision = snapshot.Graph.Revision + 1,
        };
        provider.SetGraph(updatedGraph);

        // Cutover bridge: until WMS/WMTS/FeatureServer-temporal handlers migrate off
        // the v1 catalog, mirror the relevant CatalogMetadata fields onto the v1
        // honua.layers.metadata JSONB column so v1 reads agree with V2 writes.
        var bridge = new CatalogMetadata
        {
            AccessPolicy = clearAccessPolicy ? null : accessPolicy,
            TimeInfo = clearTemporal
                ? null
                : (temporal is null
                    ? null
                    : new LayerTimeInfo
                    {
                        StartTimeField = temporal.StartTimeField,
                        EndTimeField = temporal.EndTimeField,
                        TrackIdField = temporal.TrackIdField,
                    }),
            PermanentFilter = clearPermanentFilter
                ? null
                : (permanentFilter is null
                    ? null
                    : new LayerPermanentFilter
                    {
                        Expression = permanentFilter.Expression,
                        Language = permanentFilter.Language,
                    }),
            Extrusion = clearExtrusion ? null : extrusion,
        };
        WriteV1LayerCatalogBridge(layerIndex, bridge);
    }

    /// <summary>
    /// V2-aware helper that renames the canonical resource bound to the publication
    /// with <paramref name="layerIndex"/>. The CITE-conformance WMS tests rename the
    /// v1 <c>honua.layers.layer_name</c> column to drive conformance behaviour keyed on
    /// the layer's display name (e.g. <c>cite:Autos</c>); once a handler reads its layer
    /// names from the V2 graph instead of <see cref="ILayerCatalog"/>, the same tests
    /// must update the V2 resource's <see cref="MetadataV2ObjectMetadata.Name"/> for the
    /// rename to be visible.
    /// </summary>
    public void UpdateV2ResourceName(int layerIndex, string newName)
    {
        var provider = GetService<IMetadataV2GraphProvider>() as Honua.TestKit.Infrastructure.TestMetadataV2GraphProvider
            ?? throw new InvalidOperationException(
                "Test V2 graph provider not registered as TestMetadataV2GraphProvider.");

        var snapshot = provider.GetCurrentAsync().AsTask().GetAwaiter().GetResult();
        var pub = snapshot.Graph.Publications.FirstOrDefault(p => p.LayerIndex == layerIndex);
        if (pub is null)
        {
            throw new InvalidOperationException(
                $"No publication for layer {layerIndex} in the test V2 graph.");
        }

        var resources = snapshot.Graph.Resources.ToArray();
        var mutated = false;
        for (var i = 0; i < resources.Length; i++)
        {
            if (!string.Equals(resources[i].Metadata.Id, pub.ResourceId, StringComparison.Ordinal))
            {
                continue;
            }

            resources[i] = resources[i] with
            {
                Metadata = resources[i].Metadata with { Name = newName }
            };
            mutated = true;
        }

        if (!mutated)
        {
            return;
        }

        var updatedGraph = snapshot.Graph with
        {
            Resources = resources,
            Revision = snapshot.Graph.Revision + 1,
        };
        provider.SetGraph(updatedGraph);
    }

    /// <summary>
    /// V2-aware helper that mirrors the v1 <c>IServiceMetadataUpdater</c> seed surface
    /// for service-level toggles (enabled protocols, access policy). Updates every V2
    /// service that shares the supplied name (case-insensitive) — the test graph
    /// registers one service per protocol cluster, all keyed off the same logical name,
    /// so the v1 "one row per service" admin shape collapses onto fan-out at write time.
    /// </summary>
    public void UpdateV2ServiceMetadata(
        string serviceName,
        IReadOnlyList<string>? enabledProtocols = null,
        AccessPolicy? accessPolicy = null,
        bool clearAccessPolicy = false)
    {
        var provider = GetService<IMetadataV2GraphProvider>() as Honua.TestKit.Infrastructure.TestMetadataV2GraphProvider
            ?? throw new InvalidOperationException(
                "Test V2 graph provider not registered as TestMetadataV2GraphProvider.");

        var snapshot = provider.GetCurrentAsync().AsTask().GetAwaiter().GetResult();
        var services = snapshot.Graph.Services.ToArray();
        var mutated = false;
        for (var i = 0; i < services.Length; i++)
        {
            if (!string.Equals(services[i].Metadata.Name, serviceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var next = services[i];
            if (enabledProtocols is not null)
            {
                next = next with { Protocols = enabledProtocols };
            }
            if (clearAccessPolicy)
            {
                next = next with { AccessPolicy = null };
            }
            else if (accessPolicy is not null)
            {
                next = next with { AccessPolicy = accessPolicy };
            }

            services[i] = next;
            mutated = true;
        }

        if (!mutated)
        {
            return;
        }

        var updatedGraph = snapshot.Graph with
        {
            Services = services,
            Revision = snapshot.Graph.Revision + 1,
        };
        provider.SetGraph(updatedGraph);

        // Cutover bridge: mirror EnabledProtocols / AccessPolicy onto the v1 services
        // row until protocol gating finishes migrating off the v1 catalog.
        var bridge = new CatalogMetadata
        {
            AccessPolicy = clearAccessPolicy ? null : accessPolicy,
            EnabledProtocols = enabledProtocols?.ToArray() ?? ServiceProtocols.All,
        };
        WriteV1ServiceCatalogBridge(serviceName, bridge);
    }

    /// <summary>
    /// Creates a simple test point geometry as WKB
    /// </summary>
    private static byte[] CreateTestPointGeometry(double x, double y)
    {
        // Create a simple WKB point geometry
        // This is a simplified implementation for testing purposes
        var geometryFactory = new NetTopologySuite.Geometries.GeometryFactory();
        var point = geometryFactory.CreatePoint(new NetTopologySuite.Geometries.Coordinate(x, y));
        var writer = new NetTopologySuite.IO.WKBWriter();
        return writer.Write(point);
    }

    private async Task InitializeSharedAsync()
    {
        await _sharedLock.WaitAsync();
        try
        {
            if (!_sharedInitialized)
            {
                Environment.SetEnvironmentVariable("HONUA_TEST_SCHEMA_HEADERS", "true");

                _sharedPostgres = new PostgresFixture();
                await _sharedPostgres.InitializeAsync();

                var attachmentsPath = Path.Combine(Directory.GetCurrentDirectory(), "tmp", "attachments");

                _sharedFactory = new WebApplicationFactory<Program>()
                    .WithWebHostBuilder(builder =>
                    {
                        builder.UseEnvironment("Test");
                        // honua-server#1144: dev-auth bypass requires explicit ack.
                        builder.UseSetting("HONUA_DEV_AUTH", "true");
                        builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "true");
                        builder.UseSetting("HONUA_ADMIN_PASSWORD", SharedAdminPassword);
                        builder.UseSetting("HONUA_SKIP_MIGRATIONS", "true");

                        builder.ConfigureAppConfiguration((context, configBuilder) =>
                        {
                            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                ["ConnectionStrings:honua"] = _sharedPostgres.ConnectionString,
                                ["ConnectionStrings:DefaultConnection"] = _sharedPostgres.ConnectionString,
                                // Avoid live DNS dependency during startup validation in integration tests.
                                ["Geocoding:Nominatim:BaseUrl"] = StableTestGeocodingBaseUrl,
                                ["Geocoding:Providers:Nominatim:BaseUrl"] = StableTestGeocodingBaseUrl,
                                ["HONUA_DEV_AUTH"] = "true",
                                ["HONUA_DEV_AUTH_ALLOW_BYPASS"] = "true",
                                ["HONUA_ADMIN_PASSWORD"] = SharedAdminPassword,
                                ["HONUA_SKIP_MIGRATIONS"] = "true",
                                ["HONUA_TEST_SCHEMA_HEADERS"] = "true",
                                ["Limits:Connections:RequestTimeout"] = "00:05:00",
                                ["Limits:Query:QueryTimeout"] = "00:02:00",
                                ["Database:QueryCache:EnableAutomaticCaching"] = "false",
                                ["FileStorage:Provider"] = "Local",
                                ["FileStorage:LocalStorage:BasePath"] = attachmentsPath,
                                ["Security:ConnectionEncryption:MasterKey"] = TestEncryptionMasterKey,
                                ["Security:ConnectionEncryption:Salt"] = TestEncryptionSalt
                            });
                        });

                        builder.ConfigureTestServices(services =>
                        {
                            services.RemoveAll<NpgsqlDataSource>();
                            services.RemoveAll<IFeatureReader>();
                            services.RemoveAll<IFeatureWriter>();
                            services.RemoveAll<ITileProvider>();
                            services.RemoveAll<IRelationshipStore>();
                            services.RemoveAll<IStreamingFeatureStore>();
                            services.RemoveAll<IAttachmentStore>();
                            services.RemoveAll<ILayerCatalog>();
                            services.RemoveAll<ITableDiscoveryService>();
                            services.RemoveAll<IDatabaseHealthChecker>();
                            services.RemoveAll<IDatabaseConnectionProvider>();
                            services.RemoveAll<ICrsDetectionService>();
                            services.RemoveAll<IFileImportService>();
                            services.RemoveAll<ISqlFilterTranslator>();

                            var testConfiguration = new ConfigurationBuilder()
                                .AddInMemoryCollection(new Dictionary<string, string?>
                                {
                                    ["ConnectionStrings:DefaultConnection"] = _sharedPostgres.ConnectionString,
                                    ["HONUA_TEST_SCHEMA_HEADERS"] = "true",
                                    ["Limits:Connections:RequestTimeout"] = "00:05:00",
                                    ["Limits:Query:QueryTimeout"] = "00:02:00",
                                    ["Security:ConnectionEncryption:MasterKey"] = TestEncryptionMasterKey,
                                    ["Security:ConnectionEncryption:Salt"] = TestEncryptionSalt
                                })
                                .Build();

                            Honua.Postgres.ServiceCollectionExtensions.AddPostgreSqlServices(services, testConfiguration);

                            services.RemoveAll<NpgsqlDataSource>();
                            services.AddSingleton<NpgsqlDataSource>(_ =>
                            {
                                var dataSourceBuilder = new NpgsqlDataSourceBuilder(_sharedPostgres.ConnectionString);
                                dataSourceBuilder.ConnectionStringBuilder.Multiplexing = false;
                                return dataSourceBuilder.Build();
                            });

                            services.RemoveAll<ILayerCatalog>();
                            services.AddScoped<ILayerCatalog, Honua.Postgres.Features.Catalog.PostgresLayerCatalog>();

                            // Replace the Postgres-backed Metadata v2 provider with an in-memory
                            // fixture so endpoints that have migrated off ILayerCatalog can read a
                            // snapshot without a migrated snapshot row being present in the test
                            // database.
                            RegisterDefaultMetadataV2Graph(services);
                        });
                    });

                _sharedInitialized = true;
            }

            _sharedRefCount++;
        }
        finally
        {
            _sharedLock.Release();
        }

        if (string.IsNullOrWhiteSpace(_currentSchema))
        {
            _currentSchema = await Postgres.CreateIsolatedSchemaAsync(nameof(WebAppFixture));
        }

        await SeedSchemaAsync(_currentSchema);

        Client = CreateAdminClient();
        _serviceScope = _sharedFactory?.Services.CreateScope();

        await EnsureTestSecureConnectionAsync();
    }

    public async Task DisposeAsync()
    {
        _serviceScope?.Dispose();
        _serviceScope = null;

        if (_useSharedServer)
        {
            if (_currentSchema is not null)
            {
                await Postgres.DropSchemaAsync(_currentSchema);
            }

            Client.Dispose();

            await _sharedLock.WaitAsync();
            try
            {
                if (_sharedRefCount > 0)
                {
                    _sharedRefCount--;
                }

                if (_sharedRefCount == 0 && _sharedInitialized)
                {
                    if (_sharedFactory is not null)
                    {
                        await _sharedFactory.DisposeAsync();
                    }

                    if (_sharedPostgres is not null)
                    {
                        await _sharedPostgres.DisposeAsync();
                    }

                    _sharedFactory = null;
                    _sharedPostgres = null;
                    _sharedInitialized = false;
                }
            }
            finally
            {
                _sharedLock.Release();
            }

            return;
        }

        if (_currentSchema is not null)
        {
            await Postgres.DropSchemaAsync(_currentSchema);
        }

        Client.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    /// <summary>
    /// Configure services before initialization (must be called before InitializeAsync).
    /// </summary>
    public WebAppFixture ConfigureServices(Action<IServiceCollection> configure)
    {
        _serviceConfigurations.Add(configure);
        return this;
    }

    /// <summary>
    /// Configure the web host before initialization.
    /// </summary>
    public WebAppFixture ConfigureWebHost(Action<IWebHostBuilder> configure)
    {
        _configureWebHost = _configureWebHost == null ? configure : _configureWebHost + configure;
        return this;
    }

    /// <summary>
    /// Configure a seed file to apply when creating the test schema.
    /// Must be called before InitializeAsync.
    /// </summary>
    public WebAppFixture UseSeed(string seedPath, string? profile = null)
    {
        _seedPath = ResolveSeedPath(seedPath);
        _seedProfile = profile;
        return this;
    }

    /// <summary>
    /// Replace a service in the DI container with a test implementation.
    /// </summary>
    public WebAppFixture ReplaceService<TService, TImplementation>()
        where TService : class
        where TImplementation : class, TService
    {
        _serviceConfigurations.Add(services =>
        {
            services.RemoveAll<TService>();
            services.AddScoped<TService, TImplementation>();
        });
        return this;
    }

    /// <summary>
    /// Replace a service with a specific instance.
    /// </summary>
    public WebAppFixture ReplaceService<TService>(TService instance)
        where TService : class
    {
        _serviceConfigurations.Add(services =>
        {
            services.RemoveAll<TService>();
            services.AddSingleton(instance);
        });
        return this;
    }

    /// <summary>
    /// Get a service from the test server's DI container.
    /// </summary>
    public T GetService<T>() where T : notnull
    {
        var provider = _serviceScope?.ServiceProvider
            ?? throw new InvalidOperationException("Service scope not initialized.");

        return provider.GetRequiredService<T>();
    }

    /// <summary>
    /// Get an optional service from the test server's DI container.
    /// </summary>
    public T? GetOptionalService<T>() where T : class
    {
        return _serviceScope?.ServiceProvider.GetService<T>();
    }

    /// <summary>
    /// Get the test secure connection ID created by the fixture.
    /// </summary>
    public async Task<Guid?> GetTestSecureConnectionIdAsync()
    {
        if (_serviceScope is null)
        {
            return null;
        }

        var registry = _serviceScope.ServiceProvider.GetService<ISecureConnectionRegistry>();
        if (registry == null)
        {
            return null;
        }

        var connection = await registry.GetConnectionByNameAsync(TestSecureConnectionName);
        return connection?.ConnectionId;
    }

    /// <summary>
    /// Create an isolated schema for this test.
    /// Schema is automatically cleaned up on dispose.
    /// </summary>
    public async Task<string> CreateIsolatedSchemaAsync(string testClassName)
    {
        if (!string.IsNullOrWhiteSpace(_currentSchema))
        {
            return _currentSchema;
        }

        _currentSchema = await Postgres.CreateIsolatedSchemaAsync(testClassName);
        await SeedSchemaAsync(_currentSchema);
        await EnsureTestSecureConnectionAsync();
        return _currentSchema;
    }

    private async Task EnsureTestSecureConnectionAsync()
    {
        if (_serviceScope is null)
        {
            return;
        }

        var services = _serviceScope.ServiceProvider;
        var connectionProvider = services.GetRequiredService<IDatabaseConnectionProvider>();

        if (!await SecureConnectionTablesAvailableAsync(connectionProvider).ConfigureAwait(false))
        {
            return;
        }

        await EnsureSecureConnectionProviderColumnAsync(connectionProvider).ConfigureAwait(false);

        var registry = services.GetRequiredService<ISecureConnectionRegistry>();
        var encryptionService = services.GetRequiredService<IConnectionEncryptionService>();
        var configuration = services.GetRequiredService<IConfiguration>();
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        await _secureConnectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var existing = await registry.GetConnectionByNameAsync(TestSecureConnectionName).ConfigureAwait(false);
            if (existing != null)
            {
                return;
            }

            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(builder.Host) ||
                string.IsNullOrWhiteSpace(builder.Database) ||
                string.IsNullOrWhiteSpace(builder.Username) ||
                builder.Port <= 0)
            {
                return;
            }

            var encrypted = await encryptionService.EncryptConnectionStringAsync(connectionString).ConfigureAwait(false);
            var keyVersion = await encryptionService.GetCurrentKeyVersionAsync().ConfigureAwait(false);
            var sslRequired = builder.SslMode is Npgsql.SslMode.Require or Npgsql.SslMode.VerifyCA or Npgsql.SslMode.VerifyFull;
            var sslMode = Enum.Parse<CoreSslMode>(builder.SslMode.ToString(), true);

            var connection = DataConnection.CreateWithEncryptedCredentials(
                name: TestSecureConnectionName,
                host: builder.Host,
                port: builder.Port,
                databaseName: builder.Database,
                username: builder.Username,
                encryptedConnectionString: encrypted,
                encryptionKeyVersion: keyVersion,
                createdBy: TestSecureConnectionCreatedBy,
                description: "Test secure connection",
                sslRequired: sslRequired,
                sslMode: sslMode);

            try
            {
                await registry.CreateConnectionAsync(connection).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (string.Equals(ex.SqlState, "23505", StringComparison.Ordinal))
            {
                // Another fixture created the same connection concurrently.
            }
        }
        finally
        {
            _secureConnectionLock.Release();
        }
    }

    private static async Task<bool> SecureConnectionTablesAvailableAsync(IDatabaseConnectionProvider connectionProvider)
    {
        const string sql = """
            SELECT 1
            FROM information_schema.tables
            WHERE table_schema = 'honua'
              AND table_name = 'data_connections'
            LIMIT 1
            """;
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await using var connection = await connectionProvider.OpenConnectionAsync().ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.CommandTimeout = 10;
                var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
                return result != null && result != DBNull.Value;
            }
            catch (Exception ex) when (IsTransientSecureConnectionCheckFailure(ex))
            {
                if (attempt == maxAttempts)
                {
                    Console.Error.WriteLine($"WARNING: Could not verify secure-connection table after {maxAttempts} attempts. Proceeding without it.");
                    return false;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt)).ConfigureAwait(false);
            }
        }

        Console.Error.WriteLine($"WARNING: Could not verify secure-connection table after {maxAttempts} attempts. Proceeding without it.");
        return false;
    }

    private static async Task EnsureSecureConnectionProviderColumnAsync(IDatabaseConnectionProvider connectionProvider)
    {
        const string sql = """
            ALTER TABLE IF EXISTS honua.data_connections
                ADD COLUMN IF NOT EXISTS provider_name TEXT NOT NULL DEFAULT 'postgis';
            """;

        await using var connection = await connectionProvider.OpenConnectionAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 10;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static bool IsTransientSecureConnectionCheckFailure(Exception ex)
    {
        return ex is TimeoutException or TaskCanceledException or NpgsqlException;
    }

    private Task SeedSchemaAsync(string schemaName)
    {
        if (!string.IsNullOrWhiteSpace(_seedPath))
        {
            return Postgres.ApplySeedAsync(_seedPath, schemaName, _seedProfile);
        }

        return ServerTestData.SeedAsync(Postgres, schemaName);
    }

    private static string ResolveSeedPath(string seedPath)
    {
        if (Path.IsPathRooted(seedPath) || File.Exists(seedPath))
        {
            return seedPath;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        if (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, seedPath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return seedPath;
    }


    /// <summary>
    /// Reset database state in the public schema (legacy method).
    /// Prefer schema-based isolation for parallel execution.
    /// </summary>
    public async Task ResetAsync()
    {
        await Postgres.ResetAsync();
    }

    /// <summary>
    /// Creates an <see cref="HttpMessageHandler"/> connected to the in-memory test server.
    /// Useful for constructing custom transports (e.g. gRPC channels) that route
    /// through the same pipeline as <see cref="Client"/>.
    /// </summary>
    public HttpMessageHandler CreateHandler()
    {
        var factory = (_useSharedServer ? _sharedFactory : _factory)
            ?? throw new InvalidOperationException("Web application factory not initialized.");

        return factory.Server.CreateHandler();
    }

    /// <summary>
    /// Creates a <see cref="Microsoft.AspNetCore.TestHost.WebSocketClient"/> for testing
    /// WebSocket endpoints through the in-memory test server.
    /// </summary>
    public Microsoft.AspNetCore.TestHost.WebSocketClient CreateWebSocketClient()
    {
        var factory = (_useSharedServer ? _sharedFactory : _factory)
            ?? throw new InvalidOperationException("Web application factory not initialized.");

        return factory.Server.CreateWebSocketClient();
    }

    /// <summary>
    /// Create a new HTTP client with custom configuration.
    /// </summary>
    public HttpClient CreateClient(Action<HttpClient>? configure = null)
    {
        var factory = (_useSharedServer ? _sharedFactory : _factory)
            ?? throw new InvalidOperationException("Web application factory not initialized.");

        var client = factory.CreateClient();
        client.Timeout = _defaultTestClientTimeout;
        if (_useSharedServer && !string.IsNullOrWhiteSpace(_currentSchema))
        {
            client.DefaultRequestHeaders.Add("X-Honua-Test-Schema", _currentSchema);
        }
        configure?.Invoke(client);
        return client;
    }

    /// <summary>
    /// Create a new HTTP client with admin authorization for testing admin endpoints.
    /// </summary>
    public HttpClient CreateAdminClient()
    {
        return CreateClient(client =>
        {
            client.DefaultRequestHeaders.Add("X-API-Key", SharedAdminPassword);
        });
    }

    /// <summary>
    /// Create a new HTTP client scoped to a specific database schema.
    /// </summary>
    public HttpClient CreateClient(string schemaName)
    {
        if (!string.IsNullOrWhiteSpace(schemaName))
        {
            _currentSchema = schemaName;
        }

        var client = CreateClient();
        if (_useSharedServer && !string.IsNullOrWhiteSpace(schemaName))
        {
            client.DefaultRequestHeaders.Remove("X-Honua-Test-Schema");
            client.DefaultRequestHeaders.Add("X-Honua-Test-Schema", schemaName);
        }

        return client;
    }

}
