// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Collaboration;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Ogc.Api.Features;
using Honua.Protocols.Ogc.Api.Features.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Honua.ControlPlane;
using Honua.Server.Features.Collaboration.FeatureLocks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

/// <summary>
/// Proves a collaborative-editing lease is binding on the OGC API Features write
/// surface — PUT, merge-PATCH, DELETE and the transaction batch (honua-server#4402).
/// </summary>
/// <remarks>
/// The lease is claimed in the <c>("test", 0, objectId)</c> namespace: the service
/// <em>name</em> and layer id. The FeatureServer publication of this same row lives on a
/// different graph service row (<c>svc-test-feature</c>) that shares that name, so the
/// lease taken here is the same lease a GeoServices client would take —
/// <see cref="Update_AndTheGeoServicesWriteForTheSameRow_AreBlockedByOneLease"/> proves
/// one lease blocks both protocols rather than each protocol having a private namespace.
/// </remarks>
[Protocol(TestProtocols.OgcApiFeatures)]
[Collection("Database")]
public sealed class OgcFeaturesFeatureLockEnforcementTests : IAsyncLifetime
{
    private const int TestLayerId = 0;
    private const string ServiceName = "test";
    private const string Holder = "alice";

    // Test hosts share one system schema, so leases in the shared store would block other classes' edits of the
    // same object ids. These cases are about one host; the two-host case below has a lease schema of its own.
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro)
        .ReplaceService<IFeatureLockService>(new InMemoryFeatureLockService());

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.EnableV2ServiceEditingCapabilities(ServiceName, ["Create", "Update", "Delete"]);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task Replace_WhileAnotherEditorHoldsTheLease_Returns423AndLeavesTheStoredRowUnchanged()
    {
        var objectId = await _fixture.InsertFeatureAsync(TestLayerId, "ogc-original");
        await ClaimLeaseAsync(objectId);

        using var content = ReplacementBody(objectId, "ogc-intruder");
        var response = await _fixture.Client.PutAsync(
            $"/ogc/features/collections/{TestLayerId}/items/{objectId}", content);

        response.StatusCode.Should().Be(HttpStatusCode.Locked);
        (await response.Content.ReadAsStringAsync()).Should().Contain(Holder);
        (await ReadStoredNameAsync(objectId)).Should().Be(
            "ogc-original",
            "the blocked replace must not reach the database");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task Patch_WhileAnotherEditorHoldsTheLease_Returns423AndLeavesTheStoredRowUnchanged()
    {
        var objectId = await _fixture.InsertFeatureAsync(TestLayerId, "patch-original");
        await ClaimLeaseAsync(objectId);

        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/ogc/features/collections/{TestLayerId}/items/{objectId}")
        {
            Content = new StringContent(
                """{"properties":{"name":"patch-intruder"}}""",
                Encoding.UTF8,
                "application/merge-patch+json")
        };
        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Locked);
        (await ReadStoredNameAsync(objectId)).Should().Be("patch-original");
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("DELETE /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task Delete_WhileAnotherEditorHoldsTheLease_Returns423AndTheFeatureSurvives()
    {
        var objectId = await _fixture.InsertFeatureAsync(TestLayerId, "delete-original");
        await ClaimLeaseAsync(objectId);

        var response = await _fixture.Client.DeleteAsync(
            $"/ogc/features/collections/{TestLayerId}/items/{objectId}");

        response.StatusCode.Should().Be(HttpStatusCode.Locked);
        (await ReadStoredNameAsync(objectId)).Should().Be("delete-original");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task Replace_ByTheLeaseHolder_Succeeds()
    {
        var objectId = await _fixture.InsertFeatureAsync(TestLayerId, "holder-original");
        await ClaimLeaseAsync(objectId);

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/ogc/features/collections/{TestLayerId}/items/{objectId}")
        {
            Content = ReplacementBody(objectId, "holder-write")
        };
        request.Headers.Add(FeatureEditLockEnforcement.HolderHeaderName, Holder);
        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadStoredNameAsync(objectId)).Should().Be("holder-write");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    [Endpoint("PUT /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task Update_AndTheGeoServicesWriteForTheSameRow_AreBlockedByOneLease()
    {
        // One lease, two protocols. The graph models the OGC collection and the
        // FeatureServer layer as separate publications on separate service rows that share
        // the name "test"; keying the lease namespace on that name is what makes a single
        // claim protect the row no matter which surface the competing write arrives on.
        var objectId = await _fixture.InsertFeatureAsync(TestLayerId, "shared-original");
        await ClaimLeaseAsync(objectId);

        using var ogcContent = ReplacementBody(objectId, "ogc-intruder");
        var ogc = await _fixture.Client.PutAsync(
            $"/ogc/features/collections/{TestLayerId}/items/{objectId}", ogcContent);
        ogc.StatusCode.Should().Be(HttpStatusCode.Locked);

        var geoServicesJson = """{"updates":[{"attributes":{"objectid":OBJECTID,"name":"esri-intruder"}}]}"""
            .Replace("OBJECTID", objectId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        using var geoServicesContent = new StringContent(
            geoServicesJson,
            Encoding.UTF8,
            "application/json");
        var geoServices = await _fixture.Client.PostAsync(
            $"/rest/services/{ServiceName}/FeatureServer/{TestLayerId}/applyEdits", geoServicesContent);
        var geoServicesBody = await geoServices.Content.ReadAsStringAsync();
        geoServicesBody.Should().Contain(
            "1005",
            $"the same lease must produce the GeoServices FeatureLocked code: {geoServicesBody}");

        (await ReadStoredNameAsync(objectId)).Should().Be(
            "shared-original",
            "neither protocol may write through another editor's lease");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task Replace_OnASecondTask_IsRefusedWhileTheFirstTaskHoldsTheLease_AndAcceptedAfterRelease()
    {
        var leaseSchema = "feature_locks_" + Guid.NewGuid().ToString("N");
        await using var firstTask = SharedLeaseHost(leaseSchema);
        await firstTask.InitializeAsync();
        firstTask.EnableV2ServiceEditingCapabilities(ServiceName, ["Create", "Update", "Delete"]);
        await CreateLeaseTableAsync(firstTask.Postgres, leaseSchema);
        await using var secondTask = SharedLeaseHost(leaseSchema).ShareSchemaWith(firstTask);
        await secondTask.InitializeAsync();
        secondTask.EnableV2ServiceEditingCapabilities(ServiceName, ["Create", "Update", "Delete"]);
        var objectId = await firstTask.InsertFeatureAsync(TestLayerId, "two-task-original");
        var feature = FeatureRef.Canonical(ServiceName, TestLayerId, objectId);
        var firstTaskLocks = firstTask.GetService<IFeatureLockService>();
        (await firstTaskLocks.ClaimAsync(feature, new LockHolder(Holder), TimeSpan.FromMinutes(1), FeatureLockAccessContext.AuthorizedWrite))
            .IsSuccess.Should().BeTrue();
        var path = $"/ogc/features/collections/{TestLayerId}/items/{objectId}";

        using (var intruder = ReplacementBody(objectId, "two-task-intruder"))
        {
            (await secondTask.Client.PutAsync(path, intruder)).StatusCode.Should().Be(HttpStatusCode.Locked);
        }
        (await firstTask.ReadStoredFeatureNameAsync(TestLayerId, objectId)).Should().Be(
            "two-task-original", "a lease taken on one task binds the other");

        (await firstTaskLocks.ReleaseAsync(feature, new LockHolder(Holder), FeatureLockAccessContext.AuthorizedWrite)).Status
            .Should().Be(FeatureLockReleaseStatus.Released);
        using var afterRelease = ReplacementBody(objectId, "two-task-after-release");
        (await secondTask.Client.PutAsync(path, afterRelease)).IsSuccessStatusCode.Should().BeTrue();
        (await firstTask.ReadStoredFeatureNameAsync(TestLayerId, objectId)).Should().Be("two-task-after-release");
        await firstTask.Postgres.DropSchemaAsync(leaseSchema);
    }

    // A host whose PostgreSQL lease store reads and writes the given schema; two such hosts are two tasks.
    private static WebAppFixture SharedLeaseHost(string leaseSchema)
        => new WebAppFixture().WithTestLicense(HonuaEdition.Pro).ConfigureServices(services =>
        {
            services.RemoveAll<IFeatureLockService>();
            services.AddSingleton<IFeatureLockService>(provider => new PostgresFeatureLockService(
                new JobStoreDatabase(provider.GetRequiredService<NpgsqlDataSource>(), leaseSchema)));
        });

    private static async Task CreateLeaseTableAsync(PostgresFixture postgres, string leaseSchema)
    {
        await postgres.CreateSchemaUnderLockAsync(leaseSchema);
        await using var stream = typeof(PostgresFeatureLockService).Assembly.GetManifestResourceStream(
            "Honua.Server.Migrations.180_CreateFeatureLocks.sql")
            ?? throw new InvalidOperationException("Feature lock migration is missing.");
        using var reader = new StreamReader(stream);
        await postgres.ExecuteDdlUnderLockAsync((await reader.ReadToEndAsync())
            .Replace("$HonuaSchema$", new NpgsqlCommandBuilder().QuoteIdentifier(leaseSchema), StringComparison.Ordinal));
    }

    private async Task ClaimLeaseAsync(long objectId)
    {
        var claim = await _fixture.GetService<IFeatureLockService>().ClaimAsync(
            FeatureRef.Canonical(ServiceName, TestLayerId, objectId),
            new LockHolder(Holder),
            TimeSpan.FromMinutes(5),
            FeatureLockAccessContext.AuthorizedWrite);

        claim.IsSuccess.Should().BeTrue("the lease the rest of the test depends on must exist");
    }

    private Task<string?> ReadStoredNameAsync(long objectId)
        => _fixture.ReadStoredFeatureNameAsync(TestLayerId, objectId);

    private static StringContent ReplacementBody(long objectId, string name)
    {
        var feature = new GeoJsonFeature
        {
            Type = "Feature",
            Id = objectId,
            Geometry = new SimpleGeoJsonGeometry
            {
                Type = "Point",
                CoordinatesJson = "[-122.4194, 37.7749]"
            },
            Properties = new Dictionary<string, object?> { ["name"] = name }
        };

        return new StringContent(
            JsonSerializer.Serialize(feature, OgcJsonContext.Default.GeoJsonFeature),
            Encoding.UTF8,
            MediaTypes.GeoJson);
    }
}
