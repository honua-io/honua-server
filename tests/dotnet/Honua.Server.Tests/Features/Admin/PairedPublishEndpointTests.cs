// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Publishing.Descriptors;
using Honua.Core.Features.Tiles.PMTiles;
using Honua.Server.Features.Admin.PairedPublications;
using Honua.Server.Features.Admin.TileOperations;
using Honua.Infrastructure.Progress;
using Honua.Server.Tests.Infrastructure;
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
public sealed partial class PairedPublishEndpointTests(DatabaseFixtureAdapter database) : IAsyncLifetime
{
    private const string JobsRoute = "/api/v1/admin/tile-operations/jobs";
    private const string IdentityField = "name";
    private const string HiddenField = "notes";
    private readonly WebAppFixture _fixture = new WebAppFixture().WithProductionJobSubstrate();
    private readonly string _publicationId = $"paired-{Guid.NewGuid():N}"[..24];
    private static readonly JSchema DescriptorSchema = JSchema.Parse(
        File.ReadAllText(RepositoryPaths.Resolve("docs", "gis", "schemas", "publication-descriptor.v1.schema.json")));
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await database.DataSource.ClearJobSubstrateAsync();
        // The shared host otherwise authenticates every caller as an admin.
        _fixture.ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private ICloudFileStorage Storage => _fixture.GetOptionalService<ICloudFileStorage>()!;

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/tile-operations/jobs")]
    public async Task PairedPublish_Layer_WritesBothArtifactsFromOneCaptureWithoutHiddenFields()
    {
        HideField(HiddenField);

        var published = await PublishAsync(IdentityField);

        var descriptorJson = JObject.Parse(System.Text.Encoding.UTF8.GetString(await ReadBytesAsync(published.DescriptorKey)));
        descriptorJson.IsValid(DescriptorSchema, out IList<ValidationError> errors).Should().BeTrue(
            string.Join("; ", errors.Select(error => $"{error.Path}: {error.Message}")));
        var descriptor = await ReadJsonAsync<PublicationDescriptor>(published.DescriptorKey);
        descriptor.Validation.Status.Should().Be(PublicationValidation.Passed);
        PublicationDescriptorRules.Violations(descriptor).Should().BeEmpty();
        descriptor.Artifacts.Pmtiles.CaptureId.Should().Be(published.CaptureId);
        descriptor.Artifacts.Geoparquet.CaptureId.Should().Be(published.CaptureId);
        descriptor.Identity.Field.Should().Be(IdentityField);
        descriptor.Fields.Single(field => field.Name == HiddenField).Public.Should().BeFalse();
        descriptor.Artifacts.Pmtiles.Fields.Should().NotContain(HiddenField).And.Contain(IdentityField);
        descriptor.Artifacts.Geoparquet.Fields.Should().NotContain(HiddenField).And.Contain(IdentityField);

        var parquet = await ReadBytesAsync(ArtifactKey(published, descriptor.Artifacts.Geoparquet.Href));
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(parquet)).Should().Be(descriptor.Artifacts.Geoparquet.Sha256);
        using (var reader = new ParquetSharp.Arrow.FileReader(new MemoryStream(parquet)))
        {
            reader.Schema.Metadata[PairedPublicationCapture.CaptureIdMetadataKey].Should().Be(published.CaptureId);
            reader.Schema.FieldsList.Select(field => field.Name).Should().NotContain(HiddenField);
            reader.ParquetReader.FileMetaData.NumRows.Should().Be(descriptor.Capture.FeatureCount);
        }

        var archive = await ReadBytesAsync(ArtifactKey(published, descriptor.Artifacts.Pmtiles.Href));
        var header = PMTilesHeader.ReadFrom(archive);
        var metadata = JsonNode.Parse(ArchiveMetadata(archive, header))!;
        metadata[PairedPublicationCapture.CaptureIdMetadataKey]!.GetValue<string>().Should().Be(published.CaptureId);
        metadata["vector_layers"]![0]!["fields"]!.AsObject().Select(field => field.Key).Should().NotContain(HiddenField);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/tile-operations/jobs")]
    public async Task PairedPublish_Republish_SwitchesThePointerAndKeepsThePreviousVersion()
    {
        var first = await PublishAsync(IdentityField);
        var second = await PublishAsync(IdentityField);

        second.Version.Should().NotBe(first.Version);
        second.CaptureId.Should().NotBe(first.CaptureId);
        (await ReadJsonAsync<PublicationPointer>(second.PointerKey)).Version.Should().Be(second.Version);
        (await Storage.ExistsAsync(first.DescriptorKey)).Should().BeTrue("a switched-out version stays readable for rollback");
    }

    [IntegrationTheory]
    [Endpoint("POST /api/v1/admin/tile-operations/jobs")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PairedPublish_ReplayedOperation_DoesNotCaptureOrActivateAnotherVersion(bool pointerMoved)
    {
        var (status, json) = await RunAsync(IdentityField);
        using var completed = json!;
        status.Should().Be(OperationStatus.Completed);
        var original = completed.RootElement.Deserialize<TileOperationProgress>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var current = pointerMoved ? await PublishAsync(IdentityField) : original.PublishedPublication!;
        var request = new TileOperationStartRequest
        {
            Operation = "paired-publish",
            ServiceId = WebAppFixture.TestServiceId,
            LayerId = WebAppFixture.TestLayerId,
            PublicationId = _publicationId,
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
            PublishedPublication = null,
            CompletedAt = null
        }, request, scope.ServiceProvider, CancellationToken.None);

        replayed.Status.Should().Be(pointerMoved ? OperationStatus.Failed : OperationStatus.Completed);
        if (!pointerMoved)
        {
            replayed.PublishedPublication.Should().Be(original.PublishedPublication);
        }
        (await ReadJsonAsync<PublicationPointer>(current.PointerKey)).Version.Should().Be(current.Version);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/tile-operations/jobs")]
    public async Task BeginOperation_OverlappingAttempts_ReservesOnlyOneArtifactVersion()
    {
        var options = _fixture.Services.GetRequiredService<IOptions<CloudStorageOptions>>().Value;
        var store = PairedPublicationStore.Create(Storage, options, _publicationId);
        var run = new PairedPublicationRun(_publicationId, "first", "capture-first", DateTimeOffset.UtcNow);
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
    [Endpoint("POST /api/v1/admin/tile-operations/jobs")]
    public async Task PairedPublish_FailedRun_LeavesThePreviousVersionActive()
    {
        var first = await PublishAsync(IdentityField);
        HideField(IdentityField);

        var (status, json) = await RunAsync(IdentityField);

        using (json)
        {
            status.Should().Be(OperationStatus.Failed);
            Property(json!.RootElement, "errorMessage").GetString().Should().Contain("Identity field");
        }

        (await ReadJsonAsync<PublicationPointer>(first.PointerKey)).Version.Should().Be(first.Version);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/tile-operations/jobs")]
    public async Task PairedPublish_PublicationIdOfAnotherLayer_IsRefusedAndKeepsItsPointer()
    {
        var options = _fixture.Services.GetRequiredService<IOptions<CloudStorageOptions>>().Value;
        var store = PairedPublicationStore.Create(Storage, options, _publicationId);
        var foreign = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(RepositoryPaths.Resolve("docs", "gis", "schemas", "publication-descriptor.v1", "maui-county-parcels.json")),
            PublicationDescriptorJsonContext.Default.PublicationDescriptor)!;
        await store.PutJsonOnceAsync(store.VersionKey("v0", "descriptor.json"), foreign,
            PublicationDescriptorJsonContext.Default.PublicationDescriptor, CancellationToken.None);
        var pointer = new PublicationPointer(_publicationId, "v0", "v0/descriptor.json", DateTimeOffset.UtcNow);
        (await store.SwitchPointerAsync(pointer, expectedETag: null, CancellationToken.None)).Should().BeTrue();

        var (status, json) = await RunAsync(IdentityField);

        using (json)
        {
            status.Should().Be(OperationStatus.Failed);
            Property(json!.RootElement, "errorMessage").GetString().Should().Contain("different layer");
        }

        (await ReadJsonAsync<PublicationPointer>(store.PointerKey)).Version.Should().Be("v0");
    }

    [IntegrationTheory]
    [Endpoint("POST /api/v1/admin/tile-operations/jobs")]
    [InlineData("schemaName", "unmanaged_paired_source")]
    [InlineData("layerDiscriminatorColumn", "other_layer_id")]
    [InlineData("primaryKeyColumn", "other_id")]
    public async Task PairedPublish_NoncanonicalManagedBinding_IsRefusedBeforeCapture(string option, string value)
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

        (await ReadJsonAsync<PublicationPointer>(first.PointerKey)).Version.Should().Be(first.Version);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/tile-operations/jobs")]
    public async Task SwitchPointer_StaleOrMissingExpectation_IsRefused()
    {
        var options = _fixture.Services.GetRequiredService<IOptions<CloudStorageOptions>>().Value;
        var store = PairedPublicationStore.Create(Storage, options, _publicationId);
        var v1 = new PublicationPointer(_publicationId, "v1", "v1/descriptor.json", DateTimeOffset.UtcNow);

        (await store.SwitchPointerAsync(v1, expectedETag: null, CancellationToken.None)).Should().BeTrue();
        var firstTag = await store.ReadPointerETagAsync(CancellationToken.None);
        (await store.SwitchPointerAsync(v1 with { Version = "v2" }, expectedETag: null, CancellationToken.None))
            .Should().BeFalse("a create-only switch must not replace an existing pointer");
        (await store.SwitchPointerAsync(v1 with { Version = "v2" }, firstTag, CancellationToken.None)).Should().BeTrue();
        (await store.SwitchPointerAsync(v1 with { Version = "v3" }, firstTag, CancellationToken.None))
            .Should().BeFalse("a writer holding the replaced tag is stale");
        (await ReadJsonAsync<PublicationPointer>(store.PointerKey)).Version.Should().Be("v2");
    }

    [IntegrationTheory]
    [Endpoint("POST /api/v1/admin/tile-operations/jobs")]
    [InlineData("""{"operation":"paired-publish","serviceId":"test","layerId":0,"identityField":"name"}""")]
    [InlineData("""{"operation":"paired-publish","serviceId":"test","layerId":0,"publicationId":"Not_Valid","identityField":"name"}""")]
    [InlineData("""{"operation":"paired-publish","serviceId":"test","layerId":0,"publicationId":"maui-parcels"}""")]
    [InlineData("""{"operation":"paired-publish","layerId":0,"publicationId":"maui-parcels","identityField":"name"}""")]
    [InlineData("""{"operation":"paired-publish","serviceId":"test","layerId":0,"publicationId":"maui-parcels","identityField":"name","bbox":[0,0,1,1]}""")]
    public async Task StartJob_InvalidPairedPublishRequest_ReturnsBadRequest(string body)
    {
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        using var response = await _client.PostAsync(JobsRoute, content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/tile-operations/jobs")]
    public async Task StartJob_PairedPublishWithoutCredentials_IsRefused()
    {
        using var anonymous = _fixture.CreateClient();

        using var response = await anonymous.PostAsJsonAsync(JobsRoute, PublishBody(IdentityField));

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
        operation = "paired-publish",
        serviceId = WebAppFixture.TestServiceId,
        layerId = WebAppFixture.TestLayerId,
        publicationId = _publicationId,
        identityField,
        minZoom = 0,
        maxZoom = 2,
        maxTiles = 50
    };

    private async Task<PublishedPublication> PublishAsync(string identityField)
    {
        var (status, json) = await RunAsync(identityField);
        using (json)
        {
            status.Should().Be(OperationStatus.Completed, Property(json!.RootElement, "errorMessage").ToString());
            return Property(json.RootElement, "publishedPublication").Deserialize<PublishedPublication>(
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        }
    }

    private async Task<(OperationStatus? Status, JsonDocument? Json)> RunAsync(string identityField)
    {
        using var response = await _client.PostAsJsonAsync(JobsRoute, PublishBody(identityField));
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var started = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var jobId = Property(started.RootElement, "jobId").GetString()!;

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

    private static string ArtifactKey(PublishedPublication published, string href)
        => published.DescriptorKey[..(published.DescriptorKey.LastIndexOf('/') + 1)] + href;

    private async Task<byte[]> ReadBytesAsync(string key)
    {
        await using var stream = await Storage.DownloadAsync(key);
        stream.Should().NotBeNull($"object {key} must exist");
        using var copy = new MemoryStream();
        await stream!.CopyToAsync(copy);
        return copy.ToArray();
    }

    private async Task<T> ReadJsonAsync<T>(string key)
        => JsonSerializer.Deserialize(await ReadBytesAsync(key),
            (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)PublicationDescriptorJsonContext.Default.GetTypeInfo(typeof(T))!)!;

    private static JsonElement Property(JsonElement element, string name)
        => element.EnumerateObject().First(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
}
