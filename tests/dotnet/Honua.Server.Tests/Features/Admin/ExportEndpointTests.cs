// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Tests.ParquetUnitSupport;
using Microsoft.AspNetCore.Hosting;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Exports;
using Honua.Core.Features.Tiles.PMTiles;
using Honua.Server.Features.Admin.Exports;
using Honua.Server.Features.Admin.TileOperations;
using Honua.Infrastructure.Progress;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;

namespace Honua.Server.Tests.Features.Admin;

[Collection(WebAppFixtureJobSubstrateExtensions.JobSubstrateCollection)]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Tile)]
public sealed partial class ExportEndpointTests(ExportEndpointTests.ExportHost host)
    : IClassFixture<ExportEndpointTests.ExportHost>, IAsyncLifetime
{
    private const string JobsRoute = "/api/v1/admin/tile-operations/jobs";
    private const string ExportsRoute = "/api/v1/admin/exports";
    private const string IdentityField = "name";
    private const string HiddenField = "notes";
    private readonly WebAppFixture _fixture = host.Fixture;
    private readonly string _exportId = $"paired-{Guid.NewGuid():N}"[..24];
    private static readonly JSchema CollectionSchema = JSchema.Parse(
        File.ReadAllText(RepositoryPaths.Resolve("docs", "gis", "schemas", "stac-honua.v1.0.0.schema.json")));
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        host.RestoreSeededGraph();
        _client = _fixture.CreateAdminClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }

    // One host per class. Each case publishes under its own publication id. The cases that hide a
    // field or rebind the layer change the shared graph, so every case starts from the seeded graph.
    public sealed class ExportHost : IAsyncLifetime
    {
        private MetadataV2Graph _seeded = null!;
        private long _revision;

        internal WebAppFixture Fixture { get; } = new WebAppFixture().WithProductionJobSubstrate();

        public async Task InitializeAsync()
        {
            // The shared host otherwise authenticates every caller as an admin.
            Fixture.ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            });
            await Fixture.InitializeAsync();
            await Fixture.Postgres.DataSource.ClearJobSubstrateAsync();
            _seeded = Fixture.GetCurrentV2GraphSnapshot().Graph;
            _revision = _seeded.Revision;
        }

        public Task DisposeAsync() => Fixture.DisposeAsync();

        // The graph revision only moves forward, as the host's revision-keyed readers expect.
        internal void RestoreSeededGraph()
        {
            if (Fixture.GetCurrentV2GraphSnapshot().Graph.Revision == _revision)
            {
                return;
            }

            Fixture.MutateV2Graph(_ => _seeded);
            _revision = Fixture.GetCurrentV2GraphSnapshot().Graph.Revision;
        }
    }

    private ICloudFileStorage Storage => _fixture.GetOptionalService<ICloudFileStorage>()!;

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/exports")]
    public async Task Export_Layer_WritesBothArtifactsFromOneCaptureWithoutHiddenFields()
    {
        HideField(HiddenField);

        var published = await PublishAsync(IdentityField);

        var collectionJson = JObject.Parse(System.Text.Encoding.UTF8.GetString(await ReadBytesAsync(published.CollectionKey)));
        collectionJson.IsValid(CollectionSchema, out IList<ValidationError> errors).Should().BeTrue(
            string.Join("; ", errors.Select(error => $"{error.Path}: {error.Message}")));
        collectionJson.ToString().Should().NotContain($"\"{HiddenField}\"", "a non-public field appears nowhere in an Export");
        var descriptor = await ReadCollectionAsync(published.CollectionKey);
        descriptor.Validation.Status.Should().Be(ExportValidation.Passed);
        ExportVersionRules.Violations(descriptor).Should().BeEmpty();
        descriptor.Artifacts.Pmtiles.CaptureId.Should().Be(published.CaptureId);
        descriptor.Artifacts.Geoparquet.CaptureId.Should().Be(published.CaptureId);
        descriptor.Identity.Field.Should().Be(IdentityField);
        descriptor.Fields.Should().NotContain(field => field.Name == HiddenField);
        descriptor.Artifacts.Pmtiles.Fields.Should().NotContain(HiddenField).And.Contain(IdentityField);
        descriptor.Artifacts.Geoparquet.Fields.Should().NotContain(HiddenField).And.Contain(IdentityField);

        var parquet = await ReadBytesAsync(ArtifactKey(published, descriptor.Artifacts.Geoparquet.Href));
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(parquet)).Should().Be(descriptor.Artifacts.Geoparquet.Sha256);
        using (var reader = ArrowParquetTestReader.Open(parquet))
        {
            reader.Schema.Metadata[ExportFeatureCapture.CaptureIdMetadataKey].Should().Be(published.CaptureId);
            reader.Schema.FieldsList.Select(field => field.Name).Should().NotContain(HiddenField);
            reader.ParquetReader.FileMetaData.NumRows.Should().Be(descriptor.Capture.FeatureCount);
        }

        var archive = await ReadBytesAsync(ArtifactKey(published, descriptor.Artifacts.Pmtiles.Href));
        var header = PMTilesHeader.ReadFrom(archive);
        var metadata = JsonNode.Parse(ArchiveMetadata(archive, header))!;
        metadata[ExportFeatureCapture.CaptureIdMetadataKey]!.GetValue<string>().Should().Be(published.CaptureId);
        metadata["vector_layers"]![0]!["fields"]!.AsObject().Select(field => field.Key).Should().NotContain(HiddenField);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/exports")]
    public async Task Export_Republish_SwitchesThePointerAndKeepsThePreviousVersion()
    {
        var first = await PublishAsync(IdentityField);
        var second = await PublishAsync(IdentityField);

        second.Version.Should().NotBe(first.Version);
        second.CaptureId.Should().NotBe(first.CaptureId);
        (await ReadJsonAsync<ExportPointer>(second.PointerKey)).Version.Should().Be(second.Version);
        (await Storage.ExistsAsync(first.CollectionKey)).Should().BeTrue("a switched-out version stays readable for rollback");
    }

    [IntegrationTheory]
    [Endpoint("POST /api/v1/admin/exports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_ReplayedOperation_DoesNotCaptureOrActivateAnotherVersion(bool pointerMoved)
    {
        var (status, json) = await RunAsync(IdentityField);
        using var completed = json!;
        status.Should().Be(OperationStatus.Completed);
        var original = completed.RootElement.Deserialize<TileOperationProgress>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var current = pointerMoved ? await PublishAsync(IdentityField) : original.PublishedExportVersion!;
        var request = new TileOperationStartRequest
        {
            Operation = "export",
            ServiceId = WebAppFixture.TestServiceId,
            LayerId = WebAppFixture.TestLayerId,
            ExportId = _exportId,
            IdentityField = IdentityField,
            TileMatrixSetId = "WebMercatorQuad",
            MinZoom = 0,
            MaxZoom = 2,
            MaxTiles = 50
        };
        using var scope = _fixture.Services.CreateScope();
        using var systemRead = JobSecurityScope.BeginSystem();
        var core = ActivatorUtilities.CreateInstance<TileOperationExecutionCore>(scope.ServiceProvider, NullLogger.Instance, 100);

        var replayed = await core.ExecuteAsync(original with
        {
            Status = OperationStatus.Queued,
            PublishedExportVersion = null,
            CompletedAt = null
        }, request, scope.ServiceProvider, CancellationToken.None);

        replayed.Status.Should().Be(pointerMoved ? OperationStatus.Failed : OperationStatus.Completed);
        if (!pointerMoved)
        {
            replayed.PublishedExportVersion.Should().Be(original.PublishedExportVersion);
        }
        (await ReadJsonAsync<ExportPointer>(current.PointerKey)).Version.Should().Be(current.Version);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/exports")]
    public async Task BeginOperation_OverlappingAttempts_ReservesOnlyOneArtifactVersion()
    {
        var options = _fixture.Services.GetRequiredService<IOptions<CloudStorageOptions>>().Value;
        var store = ExportStore.Create(Storage, options, _exportId);
        var run = new ExportVersionRun(_exportId, "first", "capture-first", DateTimeOffset.UtcNow);
        var operationId = Guid.NewGuid().ToString("N");

        var results = await Task.WhenAll(
            store.BeginOperationAsync(operationId, run, CancellationToken.None),
            store.BeginOperationAsync(operationId, run with { Version = "second" }, CancellationToken.None));

        results.Count(result => result is null).Should().Be(1);
        var reserved = results.Single(result => result is not null)!;
        var recovered = await store.BeginOperationAsync(operationId, run with { Version = "third" }, CancellationToken.None);
        recovered.Should().Be(reserved);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/exports")]
    public async Task Export_FailedRun_LeavesThePreviousVersionActive()
    {
        var first = await PublishAsync(IdentityField);
        HideField(IdentityField);

        var (status, json) = await RunAsync(IdentityField);

        using (json)
        {
            status.Should().Be(OperationStatus.Failed);
            Property(json!.RootElement, "errorMessage").GetString().Should().Contain("Identity field");
        }

        (await ReadJsonAsync<ExportPointer>(first.PointerKey)).Version.Should().Be(first.Version);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/exports")]
    public async Task Export_ExportIdOfAnotherLayer_IsRefusedAndKeepsItsPointer()
    {
        var options = _fixture.Services.GetRequiredService<IOptions<CloudStorageOptions>>().Value;
        var store = ExportStore.Create(Storage, options, _exportId);
        var foreign = JsonNode.Parse(await File.ReadAllTextAsync(
            RepositoryPaths.Resolve("docs", "gis", "schemas", "export-collection.v1", "maui-county-parcels.json")))!.AsObject();
        await store.PutJsonOnceAsync(store.VersionKey("v0", ExportCollectionDocument.FileName), foreign,
            ExportVersionJsonContext.Default.JsonObject, CancellationToken.None);
        var pointer = new ExportPointer(_exportId, "v0", $"v0/{ExportCollectionDocument.FileName}", DateTimeOffset.UtcNow);
        (await store.SwitchPointerAsync(pointer, expectedETag: null, CancellationToken.None)).Should().BeTrue();

        var (status, json) = await RunAsync(IdentityField);

        using (json)
        {
            status.Should().Be(OperationStatus.Failed);
            Property(json!.RootElement, "errorMessage").GetString().Should().Contain("different layer");
        }

        (await ReadJsonAsync<ExportPointer>(store.PointerKey)).Version.Should().Be("v0");
    }

    [IntegrationTheory]
    [Endpoint("POST /api/v1/admin/exports")]
    [InlineData("schemaName", "unmanaged_paired_source")]
    [InlineData("layerDiscriminatorColumn", "other_layer_id")]
    [InlineData("primaryKeyColumn", "other_id")]
    public async Task Export_NoncanonicalManagedBinding_IsRefusedBeforeCapture(string option, string value)
    {
        var first = await PublishAsync(IdentityField);
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var binding = snapshot.Graph.StorageBindings.Single(item => item.Metadata.Id == "binding-layer-0");
        var options = binding.Options.ToDictionary(item => item.Key, item => item.Value);
        options[option] = JsonSerializer.SerializeToElement(value);
        _fixture.Services.GetRequiredService<TestMetadataV2GraphProvider>().SetGraph(snapshot.Graph with
        {
            Revision = snapshot.Revision + 1,
            StorageBindings = snapshot.Graph.StorageBindings.Select(item => item == binding
                ? item with { Options = options } : item).ToArray()
        }, schema: _fixture.MetadataGraphSchema);

        var (status, json) = await RunAsync(IdentityField);

        using (json)
        {
            status.Should().Be(OperationStatus.Failed);
            Property(json!.RootElement, "errorMessage").GetString().Should().Contain("canonical managed relation");
        }

        (await ReadJsonAsync<ExportPointer>(first.PointerKey)).Version.Should().Be(first.Version);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/exports")]
    public async Task SwitchPointer_StaleOrMissingExpectation_IsRefused()
    {
        var options = _fixture.Services.GetRequiredService<IOptions<CloudStorageOptions>>().Value;
        var store = ExportStore.Create(Storage, options, _exportId);
        var v1 = new ExportPointer(_exportId, "v1", $"v1/{ExportCollectionDocument.FileName}", DateTimeOffset.UtcNow);

        (await store.SwitchPointerAsync(v1, expectedETag: null, CancellationToken.None)).Should().BeTrue();
        var firstTag = await store.ReadPointerETagAsync(CancellationToken.None);
        (await store.SwitchPointerAsync(v1 with { Version = "v2" }, expectedETag: null, CancellationToken.None))
            .Should().BeFalse("a create-only switch must not replace an existing pointer");
        (await store.SwitchPointerAsync(v1 with { Version = "v2" }, firstTag, CancellationToken.None)).Should().BeTrue();
        (await store.SwitchPointerAsync(v1 with { Version = "v3" }, firstTag, CancellationToken.None))
            .Should().BeFalse("a writer holding the replaced tag is stale");
        (await ReadJsonAsync<ExportPointer>(store.PointerKey)).Version.Should().Be("v2");
    }

    [IntegrationTheory]
    [Endpoint("POST /api/v1/admin/exports")]
    [InlineData("Not_Valid", null)]
    [InlineData(null, "not portable")]
    public async Task CreateExport_InvalidIdOrIdentityField_ReturnsBadRequest(string? exportId, string? identityField)
    {
        using var response = await _client.PostAsJsonAsync(ExportsRoute, new
        {
            source = new { kind = "layer", serviceId = WebAppFixture.TestServiceId, layerId = WebAppFixture.TestLayerId },
            exportId,
            identityField
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTheory]
    [Endpoint("POST /api/v1/admin/exports")]
    [InlineData("upload", HttpStatusCode.UnprocessableEntity)]
    [InlineData("layer", HttpStatusCode.NotFound)]
    public async Task CreateExport_UnsupportedOrUnknownSource_IsRefused(string kind, HttpStatusCode expected)
    {
        using var response = await _client.PostAsJsonAsync(ExportsRoute, new
        {
            source = new { kind, serviceId = WebAppFixture.TestServiceId, layerId = 987654 }
        });

        response.StatusCode.Should().Be(expected);
        Property(JsonElement.Parse(await response.Content.ReadAsStringAsync()), "success").GetBoolean().Should().BeFalse();
    }

    [IntegrationTheory]
    [Endpoint("POST /api/v1/admin/tile-operations/jobs")]
    [InlineData("export")]
    [InlineData("paired-publish")]
    public async Task StartJob_ExportOperation_IsRefusedAndNamesTheExportsRoute(string operation)
    {
        using var response = await _client.PostAsJsonAsync(JobsRoute, new
        {
            operation,
            serviceId = WebAppFixture.TestServiceId,
            layerId = WebAppFixture.TestLayerId
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(ExportsRoute);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/exports")]
    public async Task CreateExport_WithoutCredentials_IsRefused()
    {
        using var anonymous = _fixture.CreateClient();

        using var response = await anonymous.PostAsJsonAsync(ExportsRoute, PublishBody(IdentityField));

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    private void HideField(string name)
    {
        var field = _fixture.GetCurrentV2GraphSnapshot().Index.ResourcesByStorageLayerId[WebAppFixture.TestLayerId]
            .SchemaFields.Single(candidate => candidate.Name == name);
        _fixture.UpdateV2ResourceSchemaField(WebAppFixture.TestLayerId, field with { Hidden = true });
    }

    private object PublishBody(string identityField) => new
    {
        source = new { kind = "layer", serviceId = WebAppFixture.TestServiceId, layerId = WebAppFixture.TestLayerId },
        exportId = _exportId,
        identityField,
        minZoom = 0,
        maxZoom = 2
    };

    private async Task<PublishedExportVersion> PublishAsync(string identityField)
    {
        var (status, json) = await RunAsync(identityField);
        using (json)
        {
            status.Should().Be(OperationStatus.Completed, Property(json!.RootElement, "errorMessage").ToString());
            return Property(json.RootElement, "publishedExportVersion").Deserialize<PublishedExportVersion>(
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        }
    }

    private async Task<(OperationStatus? Status, JsonDocument? Json)> RunAsync(string identityField)
    {
        using var response = await _client.PostAsJsonAsync(ExportsRoute, PublishBody(identityField));
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        using var started = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var jobId = Property(Property(started.RootElement, "data"), "jobId").GetString()!;

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var json = JsonDocument.Parse(await _client.GetStringAsync($"{JobsRoute}/{jobId}"));
            var status = (OperationStatus)Property(json.RootElement, "status").GetInt32();
            if (status is OperationStatus.Completed or OperationStatus.Failed)
            {
                return (status, json);
            }

            json.Dispose();
            await Task.Delay(200);
        }

        return (null, null);
    }

    private static byte[] ArchiveMetadata(byte[] archive, PMTilesHeader header)
    {
        var raw = archive.AsSpan(checked((int)header.JsonMetadataOffset), checked((int)header.JsonMetadataLength)).ToArray();
        if (header.InternalCompression != PMTilesCompression.Gzip)
        {
            return raw;
        }

        using var gzip = new System.IO.Compression.GZipStream(new MemoryStream(raw), System.IO.Compression.CompressionMode.Decompress);
        using var inflated = new MemoryStream();
        gzip.CopyTo(inflated);
        return inflated.ToArray();
    }

    private static string ArtifactKey(PublishedExportVersion published, string href)
        => published.CollectionKey[..(published.CollectionKey.LastIndexOf('/') + 1)] + href;

    private async Task<byte[]> ReadBytesAsync(string key)
    {
        await using var stream = await Storage.DownloadAsync(key);
        stream.Should().NotBeNull($"object {key} must exist");
        using var copy = new MemoryStream();
        await stream!.CopyToAsync(copy);
        return copy.ToArray();
    }

    private async Task<ExportVersion> ReadCollectionAsync(string key)
        => ExportCollectionDocument.Read(JsonNode.Parse(await ReadBytesAsync(key))!.AsObject())!;

    private async Task<T> ReadJsonAsync<T>(string key)
        => JsonSerializer.Deserialize(await ReadBytesAsync(key),
            (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)ExportVersionJsonContext.Default.GetTypeInfo(typeof(T))!)!;

    private static JsonElement Property(JsonElement element, string name)
        => element.EnumerateObject().First(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
}
