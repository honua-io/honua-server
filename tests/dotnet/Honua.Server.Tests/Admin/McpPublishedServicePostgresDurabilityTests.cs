// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Caching;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Db.Postgres.Features.Admin;
using Honua.Db.Postgres.Features.Metadata;
using Honua.Infrastructure.MultiTenancy;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Operations;
using Honua.Server.Startup;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using CoreSslMode = Honua.Core.Features.Security.Domain.SslMode;

namespace Honua.Server.Tests.Admin;

/// <summary>
/// Publishes against the production Postgres metadata store, then destroys and
/// recreates the HTTP host before reading the exact advertised MCP resource.
/// The usual WebAppFixture substitutes an in-memory metadata graph; this class
/// restores only the production graph registrations in its isolated host.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
public sealed class McpPublishedServicePostgresDurabilityTests : IAsyncLifetime
{
    private readonly string _environment = $"mcpdur-{Guid.NewGuid():N}";
    private readonly string _serviceName = $"mcpdur_{Guid.NewGuid():N}";
    private readonly string _firstTable = $"mcpdur_first_{Guid.NewGuid():N}";
    private readonly string _secondTable = $"mcpdur_second_{Guid.NewGuid():N}";
    private readonly WebAppFixture _fixture;
    private readonly List<int> _publishedLayerIds = [];
    private HttpClient _client = null!;
    private Guid _connectionId;

    public McpPublishedServicePostgresDurabilityTests()
    {
        _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);
        _fixture.ConfigureServices(services =>
        {
            services.RemoveAll<IMetadataV2GraphProvider>();
            services.RemoveAll<IMetadataV2GraphStore>();
            services.AddScoped<IMetadataV2GraphStore>(provider => new PostgresMetadataV2GraphStore(
                provider.GetRequiredService<IAdoNetDatabaseConnectionProvider>(),
                _environment,
                provider.GetRequiredService<IDatabaseSchemaGuard>(),
                schemaName: "honua",
                cacheInvalidator: provider.GetRequiredService<IMetadataV2GraphCacheInvalidator>()));
            services.AddScoped<IMetadataV2GraphProvider>(provider => new CachingMetadataV2GraphProvider(
                provider.GetRequiredService<IMetadataV2GraphStore>(),
                provider.GetRequiredService<MetadataV2GraphSnapshotCache>(),
                _environment,
                provider.GetRequiredService<IServiceScopeFactory>(),
                static scope => scope.GetRequiredService<IMetadataV2GraphStore>()));
            // Test skips production infrastructure registration; this callback
            // runs after its real graph provider is installed by the fixture.
            services.AddMcpCatalogPublicationSurface();
            services.Configure<LayerPublishingOptions>(options => options.AsyncPublishRowThreshold = 1);
        });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        // Test hosts skip DbUp, and the shared seed only supplies the snapshot
        // tables. Install the complete production Metadata v2 migration before
        // the real store writes its five sidecar indexes.
        var assembly = typeof(ServerCoreSchemaMigrations).Assembly;
        var migrationName = ServerCoreSchemaMigrations.Manifest.MetadataV2SnapshotMigration;
        await using (var stream = assembly.GetManifestResourceStream(migrationName)
            ?? throw new InvalidOperationException($"Embedded migration '{migrationName}' was not found."))
        using (var reader = new StreamReader(stream))
        {
            var ddl = (await reader.ReadToEndAsync())
                .Replace("$HonuaSchema$", "honua", StringComparison.Ordinal);
            await _fixture.Postgres.ExecuteDdlUnderLockAsync(ddl);
        }

        await McpPublicationFixtureSetup.InstallPromotionStoresAsync(_fixture);
        _client = _fixture.CreateAdminClient();
        await CreateOwnedPublishConnectionAsync();

        // A private metadata environment makes the persisted baseline and
        // teardown independent of other fixtures using the shared honua schema.
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphStore>();
            store.Should().BeOfType<PostgresMetadataV2GraphStore>();
            await store.SaveAsync(new MetadataV2Graph
            {
                Environment = _environment,
                Revision = 1,
                GeneratedAt = DateTimeOffset.UtcNow
            }, expectedEtag: null);
        }

        await _fixture.Postgres.ExecuteDdlUnderLockAsync($"""
            CREATE TABLE public.{_firstTable} (
                id integer PRIMARY KEY, name text NOT NULL, geom geometry(Point, 4326) NOT NULL);
            CREATE TABLE public.{_secondTable} (
                id integer PRIMARY KEY, name text NOT NULL, geom geometry(Point, 4326) NOT NULL);
            INSERT INTO public.{_firstTable} VALUES
                (1, 'first-authored-row', ST_SetSRID(ST_Point(1, 2), 4326));
            INSERT INTO public.{_secondTable} VALUES
                (1, 'second-authored-row-a', ST_SetSRID(ST_Point(3, 4), 4326)),
                (2, 'second-authored-row-b', ST_SetSRID(ST_Point(5, 6), 4326));
            """);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        try
        {
            if (_fixture.CurrentSchema is not null)
            {
                // Publishing also writes the shared legacy catalog. Remove only
                // this fixture's layers/service before dropping their source tables.
                await _fixture.Postgres.RunUnderSchemaMutationLockAsync(async () =>
                {
                    await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
                    await using var command = connection.CreateCommand();
                    command.CommandText = """
                        DELETE FROM features
                        WHERE layer_id = ANY(@layerIds)
                           OR layer_id IN (
                               SELECT layer_id FROM honua.service_layers WHERE service_name = @serviceName);
                        DELETE FROM honua.layers
                        WHERE layer_id = ANY(@layerIds)
                           OR layer_id IN (
                               SELECT layer_id FROM honua.service_layers WHERE service_name = @serviceName);
                        DELETE FROM honua.services WHERE service_name = @serviceName;
                        DELETE FROM honua.metadata_v2_current WHERE environment = @environment;
                        DELETE FROM honua.metadata_v2_snapshots WHERE environment = @environment;
                        """;
                    command.Parameters.AddWithValue("layerIds", _publishedLayerIds.ToArray());
                    command.Parameters.AddWithValue("serviceName", _serviceName);
                    command.Parameters.AddWithValue("environment", _environment);
                    await command.ExecuteNonQueryAsync();
                });
                await _fixture.Postgres.ExecuteDdlUnderLockAsync($"""
                    DROP TABLE IF EXISTS public.{_firstTable};
                    DROP TABLE IF EXISTS public.{_secondTable};
                    """);
            }
        }
        finally
        {
            try
            {
                if (_connectionId != Guid.Empty)
                {
                    await using var scope = _fixture.Services.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<ISecureConnectionRegistry>()
                        .DeleteConnectionAsync(_connectionId);
                }
            }
            finally
            {
                await _fixture.DisposeAsync();
            }
        }
    }

    private async Task CreateOwnedPublishConnectionAsync()
    {
        // Source-backed publishing also materializes rows into canonical features.
        // Match the server's isolated schema without modifying the shared fixture connection.
        var builder = new NpgsqlConnectionStringBuilder(_fixture.Postgres.ConnectionString)
        {
            SearchPath = $"{_fixture.CurrentSchema},public"
        };
        await using var scope = _fixture.Services.CreateAsyncScope();
        var encryption = scope.ServiceProvider.GetRequiredService<IConnectionEncryptionService>();
        var connection = DataConnection.CreateWithEncryptedCredentials(
            name: _serviceName,
            host: builder.Host!,
            port: builder.Port,
            databaseName: builder.Database!,
            username: builder.Username!,
            encryptedConnectionString: await encryption.EncryptConnectionStringAsync(builder.ConnectionString),
            encryptionKeyVersion: await encryption.GetCurrentKeyVersionAsync(),
            createdBy: nameof(McpPublishedServicePostgresDurabilityTests),
            sslRequired: builder.SslMode is Npgsql.SslMode.Require or Npgsql.SslMode.VerifyCA or Npgsql.SslMode.VerifyFull,
            sslMode: Enum.Parse<CoreSslMode>(builder.SslMode.ToString(), true));
        var created = await scope.ServiceProvider.GetRequiredService<ISecureConnectionRegistry>()
            .CreateConnectionAsync(connection);
        _connectionId = created.ConnectionId;
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    [Endpoint("GET /api/v1/admin/connections/{id}/layers/jobs/{jobId}")]
    [Endpoint("GET /api/v1/operations/handles/{handleId}")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task PublishTwice_QueuedCompletionAndHostRestart_ReadPersistedGraphAtExactUri()
    {
        using var errors = new McpPublicationErrorCapture();
        _fixture.Services.GetRequiredService<ILoggerFactory>().AddProvider(errors);
        (await CountSourceRowsAsync(_firstTable)).Should().Be(1);
        (await CountSourceRowsAsync(_secondTable)).Should().Be(2);
        var first = await PublishAsync(_firstTable, "first layer");
        first.GetProperty("status").GetString().Should().Be("Completed", "{0}{1}{2}",
            first.GetRawText(), Environment.NewLine, errors.Diagnostics);
        var exactUri = first.GetProperty("serviceUri").GetString();
        exactUri.Should().NotBeNullOrWhiteSpace();
        var firstStorageId = int.Parse(first.GetProperty("layerId").GetString()!, CultureInfo.InvariantCulture);
        _publishedLayerIds.Add(firstStorageId);

        var second = await PublishAsync(_secondTable, "second layer");
        second.GetProperty("status").GetString().Should().Be("Queued");
        var jobId = second.GetProperty("jobId").GetString();
        jobId.Should().NotBeNullOrWhiteSpace();
        var secondStorageId = await AwaitJobCompletionAsync(jobId!);
        _publishedLayerIds.Add(secondStorageId);

        // Re-read the accepted operation instance and ask the actual executor
        // to resolve its completed job against the persisted graph. No
        // requested name or guessed URI is substituted for its result.
        OperationStatus completed;
        {
            using var scope = _fixture.Services.CreateScope();
            var progressStore = scope.ServiceProvider.GetRequiredService<IUniversalProgressStore>();
            var progress = await progressStore.GetProgressAsync<LayerPublishJobProgress>(jobId!);
            progress.Should().NotBeNull();
            if (!string.IsNullOrWhiteSpace(progress!.TenantId))
            {
                scope.ServiceProvider.GetRequiredService<RequestTenantContext>()
                    .Set(progress.TenantId, TenantContextSource.Default);
            }

            var executor = scope.ServiceProvider.GetServices<IOperationExecutor>()
                .Single(candidate => candidate.OperationId == ServicePublishOperation.OperationId);
            var accepted = await scope.ServiceProvider.GetRequiredService<IOperationInstanceStore>()
                .GetAsync(second.GetProperty("operationInstanceId").GetString()!);
            accepted.Should().NotBeNull();
            accepted!.OperationId.Should().Be(ServicePublishOperation.OperationId);
            accepted.JobId.Should().Be(jobId);
            accepted.CorrelationId.Should().Be(second.GetProperty("correlationId").GetString());
            completed = await executor.GetStatusAsync(accepted);
            completed.Status.Should().Be(OperationHandleStatus.Completed);
            completed.Result!.Details["readbackStatus"].Should().Be("Resolved");
            completed.Result.Details["serviceUri"].Should().Be(exactUri);
            completed.Result.Details["layerId"].Should().Be(secondStorageId.ToString(CultureInfo.InvariantCulture));
        }

        using (var statusResponse = await _client.GetAsync(
                   $"/api/v1/operations/handles/{second.GetProperty("operationInstanceId").GetString()}"))
        {
            var body = await statusResponse.Content.ReadAsStringAsync();
            statusResponse.StatusCode.Should().Be(HttpStatusCode.OK, body);
            using var document = JsonDocument.Parse(body);
            var status = document.RootElement.GetProperty("data");
            status.GetProperty("status").GetString().Should().Be("Completed");
            var details = status.GetProperty("result").GetProperty("details");
            details.GetProperty("readbackStatus").GetString().Should().Be("Resolved");
            details.GetProperty("serviceUri").GetString().Should().Be(exactUri);
        }

        _client.Dispose();
        await _fixture.RestartHostAsync();
        _client = _fixture.CreateAdminClient();
        await using var freshScope = _fixture.Services.CreateAsyncScope();
        var freshStore = freshScope.ServiceProvider.GetRequiredService<IMetadataV2GraphStore>();
        freshStore.Should().BeOfType<PostgresMetadataV2GraphStore>();
        var snapshot = await freshStore.GetCurrentAsync();
        snapshot.Graph.Environment.Should().Be(_environment);
        (await ReadPersistedRevisionAsync()).Should().Be(snapshot.Revision);
        completed.MetadataRevision.Should().Be(snapshot.Revision);
        var service = snapshot.Graph.Services.Single(candidate => candidate.Metadata.Name == _serviceName);
        var publications = snapshot.Graph.Publications.Where(publication =>
            publication.ServiceId == service.Metadata.Id &&
            publication.PublicationType == MetadataV2PublicationType.EsriFeatureLayer).ToArray();
        publications.Should().HaveCount(2);
        var storageIds = publications.Select(publication =>
            snapshot.ResolveStorageBinding(publication)!.StorageLayerId).ToArray();
        storageIds.Should().BeEquivalentTo([firstStorageId, secondStorageId]);
        foreach (var publication in publications)
        {
            var resource = snapshot.ResolveResource(publication);
            resource.Should().NotBeNull();
            snapshot.ResolveStorageBinding(publication)!.ResourceId.Should().Be(resource!.Metadata.Id);
        }

        var firstPublication = publications.Single(publication =>
            snapshot.ResolveStorageBinding(publication)!.StorageLayerId == firstStorageId);
        var secondPublication = publications.Single(publication =>
            snapshot.ResolveStorageBinding(publication)!.StorageLayerId == secondStorageId);
        await AssertPublishedRowsAsync(firstPublication.LayerIndex!.Value,
            [(1, "first-authored-row", 1d, 2d)]);
        await AssertPublishedRowsAsync(secondPublication.LayerIndex!.Value,
            [(1, "second-authored-row-a", 3d, 4d), (2, "second-authored-row-b", 5d, 6d)]);

        var detail = await ReadResourceAsync(exactUri!);
        detail.GetProperty("resourceUri").GetString().Should().Be(exactUri);
        detail.GetProperty("metadataServiceId").GetString().Should().Be(service.Metadata.Id);
        detail.GetProperty("etag").GetString().Should().Be(snapshot.Etag);
        detail.GetProperty("layerIds").EnumerateArray().Select(value => value.GetInt32())
            .Should().BeEquivalentTo(publications.Select(publication => publication.LayerIndex!.Value));
        detail.GetProperty("provenance").TryGetProperty("originatingIntentId", out _).Should().BeFalse();

        var index = await ReadResourceAsync("honua://published-services");
        index.GetProperty("items").EnumerateArray()
            .Any(item => item.GetProperty("resourceUri").GetString() == exactUri)
            .Should().BeTrue();
    }

    private async Task<JsonElement> PublishAsync(string table, string layerName)
    {
        using var response = await _client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = "honua_publish_service",
                arguments = new
                {
                    connectionId = _connectionId.ToString("D"),
                    schema = "public",
                    table,
                    layerName,
                    serviceName = _serviceName,
                    geometryColumn = "geom",
                    geometryType = "Point",
                    srid = 4326,
                    primaryKey = "id",
                    fields = new[] { "id", "name" }
                }
            }
        });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse(body);
        var result = document.RootElement.GetProperty("result");
        result.TryGetProperty("isError", out var isError);
        (isError.ValueKind == JsonValueKind.True).Should().BeFalse(body);
        return result.GetProperty("structuredContent").Clone();
    }

    private async Task<long> CountSourceRowsAsync(string table)
    {
        await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM public.{table}";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> ReadPersistedRevisionAsync()
    {
        await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT revision FROM honua.metadata_v2_current WHERE environment = @environment";
        command.Parameters.AddWithValue("environment", _environment);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<int> AwaitJobCompletionAsync(string jobId)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (!deadline.IsCancellationRequested)
        {
            using var response = await _client.GetAsync(
                $"/api/v1/admin/connections/{_connectionId:D}/layers/jobs/{jobId}", deadline.Token);
            var body = await response.Content.ReadAsStringAsync(deadline.Token);
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            using var document = JsonDocument.Parse(body);
            var data = document.RootElement.GetProperty("data");
            var status = data.GetProperty("status").GetString();
            if (status == "Completed")
            {
                return data.GetProperty("layerId").GetInt32();
            }

            status.Should().NotBe("Failed", body);
            status.Should().NotBe("Cancelled", body);
            await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token);
        }

        throw new TimeoutException($"Layer publish job {jobId} did not complete within 90 seconds.");
    }

    private async Task AssertPublishedRowsAsync(
        int publicLayerId,
        IReadOnlyList<(long Id, string Name, double X, double Y)> expected)
    {
        using var response = await _client.GetAsync(
            $"/rest/services/{_serviceName}/FeatureServer/{publicLayerId}/query" +
            "?f=json&where=1%3D1&outFields=*&returnGeometry=true&resultRecordCount=10");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var rows = document.RootElement.GetProperty("features").EnumerateArray()
            .Select(feature => (
                Id: feature.GetProperty("attributes").GetProperty("id").GetInt64(),
                Name: feature.GetProperty("attributes").GetProperty("name").GetString(),
                X: feature.GetProperty("geometry").GetProperty("x").GetDouble(),
                Y: feature.GetProperty("geometry").GetProperty("y").GetDouble()))
            .ToArray();
        rows.Should().BeEquivalentTo(expected);
    }

    private async Task<JsonElement> ReadResourceAsync(string uri)
    {
        using var response = await _client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "resources/read",
            @params = new { uri }
        });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var envelope = JsonDocument.Parse(body);
        envelope.RootElement.TryGetProperty("error", out _).Should().BeFalse(body);
        var contents = envelope.RootElement.GetProperty("result").GetProperty("contents");
        contents.GetArrayLength().Should().Be(1);
        contents[0].GetProperty("uri").GetString().Should().Be(uri);
        using var payload = JsonDocument.Parse(contents[0].GetProperty("text").GetString()!);
        return payload.RootElement.Clone();
    }
}
