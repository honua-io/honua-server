// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Collaboration;
using Honua.Protocols.OData.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.OData;

/// <summary>
/// Proves a collaborative-editing lease is binding on the OData write surface
/// (honua-server#4402).
/// </summary>
/// <remarks>
/// The lease namespace is derived from the metadata graph the same way the write path
/// derives it — the routed service <em>name</em> plus the publication's layer id — rather
/// than hard-coded, so this also pins that a client claiming under the identifiers it can
/// see is claiming under the identifiers the server enforces on.
/// </remarks>
[Collection("Database")]
[Protocol(TestProtocols.ODataV4)]
public sealed class ODataFeatureLockEnforcementTests : IAsyncLifetime
{
    private const int TestLayerId = 0;
    private const string Holder = "alice";

    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Community);

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Join("tests", "seed", "odata.yaml"));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /odata/Layers({layerId})/Features({objectId})")]
    public async Task Patch_WhileAnotherEditorHoldsTheLease_Returns423AndLeavesTheStoredRowUnchanged()
    {
        var objectId = await _fixture.InsertFeatureAsync(TestLayerId, "odata-original");
        await ClaimLeaseAsync(objectId);

        var response = await PatchAsync(objectId, "odata-intruder");

        response.StatusCode.Should().Be(HttpStatusCode.Locked);
        (await response.Content.ReadAsStringAsync()).Should().Contain(Holder);
        (await ReadStoredNameAsync(objectId)).Should().Be(
            "odata-original",
            "the blocked update must not reach the database");
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("DELETE /odata/Features(LayerId={layerId},ObjectId={objectId})")]
    public async Task Delete_WhileAnotherEditorHoldsTheLease_Returns423AndTheFeatureSurvives()
    {
        var objectId = await _fixture.InsertFeatureAsync(TestLayerId, "odata-delete-original");
        await ClaimLeaseAsync(objectId);

        var response = await _fixture.Client.DeleteAsync(
            $"/odata/Features(LayerId={TestLayerId},ObjectId={objectId})");

        response.StatusCode.Should().Be(HttpStatusCode.Locked);
        (await ReadStoredNameAsync(objectId)).Should().Be("odata-delete-original");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /odata/Layers({layerId})/Features({objectId})")]
    public async Task Patch_ByTheLeaseHolder_Succeeds()
    {
        var objectId = await _fixture.InsertFeatureAsync(TestLayerId, "holder-original");
        await ClaimLeaseAsync(objectId);

        var response = await PatchAsync(objectId, "holder-write", asHolder: true);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadStoredNameAsync(objectId)).Should().Be("holder-write");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /odata/Layers({layerId})/Features({objectId})")]
    public async Task Patch_OfAnUnleasedFeature_IsUnaffected()
    {
        var leased = await _fixture.InsertFeatureAsync(TestLayerId, "leased-row");
        var free = await _fixture.InsertFeatureAsync(TestLayerId, "free-row");
        await ClaimLeaseAsync(leased);

        var response = await PatchAsync(free, "free-row-edited");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadStoredNameAsync(free)).Should().Be("free-row-edited");
        (await ReadStoredNameAsync(leased)).Should().Be("leased-row");
    }

    private async Task ClaimLeaseAsync(long objectId)
    {
        var claim = await _fixture.GetService<IFeatureLockService>().ClaimAsync(
            FeatureRef.Canonical(ResolveLeaseServiceName(), TestLayerId, objectId),
            new LockHolder(Holder),
            TimeSpan.FromMinutes(5),
            FeatureLockAccessContext.AuthorizedWrite);

        claim.IsSuccess.Should().BeTrue("the lease the rest of the test depends on must exist");
    }

    /// <summary>
    /// Resolves the lease namespace exactly as the write path does: the name of the
    /// service publishing this layer.
    /// </summary>
    private string ResolveLeaseServiceName()
    {
        var graph = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var publication = graph.Publications
            .Should().Contain(candidate => candidate.LayerIndex == TestLayerId).Subject;
        var service = graph.Services
            .Should().Contain(candidate => candidate.Metadata.Id == publication.ServiceId).Subject;

        service.Metadata.Name.Should().NotBeNullOrWhiteSpace(
            "the lease namespace is the routed service name, so a nameless service would be unclaimable");
        return service.Metadata.Name!;
    }

    private Task<string?> ReadStoredNameAsync(long objectId)
        => _fixture.ReadStoredFeatureNameAsync(TestLayerId, objectId);

    private async Task<HttpResponseMessage> PatchAsync(long objectId, string name, bool asHolder = false)
    {
        var request = new ODataFeatureRequest
        {
            Attributes = new Dictionary<string, object?> { ["name"] = name }
        };

        using var message = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/odata/Layers({TestLayerId})/Features({objectId})")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(request, ODataJsonContext.Default.ODataFeatureRequest),
                Encoding.UTF8,
                "application/json")
        };

        if (asHolder)
        {
            message.Headers.Add(FeatureEditLockEnforcement.HolderHeaderName, Holder);
        }

        return await _fixture.Client.SendAsync(message);
    }
}
