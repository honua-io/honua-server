// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Collaboration;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// End-to-end proof that a collaborative-editing lease is binding on the GeoServices
/// write path (honua-server#4402).
/// </summary>
/// <remarks>
/// <para>
/// The lease here is a <b>real</b> one: it is taken through the shipped
/// <see cref="IFeatureLockService"/> singleton — the same instance
/// <c>/api/v1/saved-maps/{mapId}/collaboration/feature-locks/claim</c> writes to — and
/// the competing edit is a real HTTP <c>applyEdits</c> against real PostGIS. Nothing
/// about the write path is substituted or decorated. This is the assertion the issue
/// named as missing: before #4402 <c>IFeatureEditGuard</c> had twenty-six passing unit
/// tests and no consumer, so every one of these edits landed and overwrote the holder.
/// </para>
/// <para>
/// <b>The lease namespace.</b> A lease is keyed on
/// <c>(serviceName, layerId, featureId)</c>. <see cref="LeaseNamespaceIsTheRouteServiceAndLayer"/>
/// pins that those are the service id and layer id from the request URL, and not some
/// internal storage identifier a client could not know — that binding is the whole
/// reason a lease claimed by a client blocks a write from another one.
/// </para>
/// </remarks>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerFeatureLockEnforcementTests : IAsyncLifetime
{
    private const string ServiceId = "test";
    private const int LayerId = 0;
    private const string Holder = "alice";

    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.EnableV2ServiceEditingCapabilities(ServiceId, ["Create", "Update", "Delete"]);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public void LeaseNamespaceIsTheRouteServiceAndLayer()
    {
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();

        // The graph gives each protocol its own service row for one logical service
        // (svc-test-feature, svc-test-map, …) but they share the NAME the URL carries.
        // Keying the lease namespace on the name is what a client can actually know, and
        // what lets a lease claimed once bind a competing write on another protocol.
        var featureService = snapshot.Graph.Services
            .Should().ContainSingle(service => service.Metadata.Id == "svc-test-feature").Subject;
        featureService.Metadata.Name.Should().Be(
            ServiceId,
            "the FeatureServer route segment is the service NAME, and that is the lease namespace");

        snapshot.Graph.Publications
            .Should().Contain(publication =>
                publication.ServiceId == featureService.Metadata.Id && publication.LayerIndex == LayerId,
                "the edited layer must resolve to the layer id the client claims its lease under");
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits, Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_UpdateWhileAnotherEditorHoldsTheLease_IsRejectedAndLeavesTheStoredRowUnchanged()
    {
        var objectId = await AddFeatureAsync("locked-original");
        await ClaimLeaseAsync(objectId);

        // No holder headers: this request is the *other* editor.
        var response = await PostApplyEditsAsync(UpdateNamePayload(objectId, "intruder-write"));
        var body = await response.Content.ReadAsStringAsync();
        response.Be200Ok();

        var result = Deserialize(body);
        result.Success.Should().BeFalse(body);
        result.UpdateResults.Should().ContainSingle(body);
        result.UpdateResults![0].Success.Should().BeFalse(body);
        result.UpdateResults[0].ObjectId.Should().Be(objectId, body);
        result.UpdateResults[0].Error!.Code.Should().Be(
            GeoServicesEditErrorCodes.FeatureLocked,
            $"a lease held by another editor is the published lock/locked class: {body}");
        result.UpdateResults[0].Error!.Description.Should().Contain(
            Holder,
            $"the refusal must name the blocking editor so a client can prompt for it: {body}");

        // The point of the whole feature: the holder's row is exactly as they left it.
        (await ReadStoredNameAsync(objectId)).Should().Be(
            "locked-original",
            "the blocked writer's value must never reach the database");
        await AssertQueriedNameAsync(objectId, "locked-original");
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits, Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_DeleteWhileAnotherEditorHoldsTheLease_IsRejectedAndTheFeatureSurvives()
    {
        var objectId = await AddFeatureAsync("locked-against-delete");
        await ClaimLeaseAsync(objectId);

        var response = await PostApplyEditsAsync(
            $$"""{"deletes":[{{objectId.ToString(CultureInfo.InvariantCulture)}}]}""");
        var body = await response.Content.ReadAsStringAsync();
        response.Be200Ok();

        var result = Deserialize(body);
        result.Success.Should().BeFalse(body);
        result.DeleteResults.Should().ContainSingle(body);
        result.DeleteResults![0].Success.Should().BeFalse(body);
        result.DeleteResults[0].Error!.Code.Should().Be(GeoServicesEditErrorCodes.FeatureLocked, body);

        (await ReadStoredNameAsync(objectId)).Should().Be(
            "locked-against-delete",
            "a leased feature must not be deleted out from under its holder");
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits, Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_UpdateByTheLeaseHolder_Succeeds()
    {
        // Enforcement must protect the holder, not lock everyone out including them.
        var objectId = await AddFeatureAsync("holder-original");
        await ClaimLeaseAsync(objectId);

        var response = await PostApplyEditsAsync(
            UpdateNamePayload(objectId, "holder-write"),
            asHolder: true);
        var body = await response.Content.ReadAsStringAsync();
        response.Be200Ok();

        Deserialize(body).UpdateResults
            .Should().ContainSingle(edit => edit.Success && edit.ObjectId == objectId, body);
        (await ReadStoredNameAsync(objectId)).Should().Be("holder-write");
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits, Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_UpdateAfterTheLeaseIsReleased_Succeeds()
    {
        // A lease is a lease, not a permanent block: releasing it reopens the row.
        var objectId = await AddFeatureAsync("released-original");
        await ClaimLeaseAsync(objectId);

        var blocked = await PostApplyEditsAsync(UpdateNamePayload(objectId, "too-early"));
        Deserialize(await blocked.Content.ReadAsStringAsync()).UpdateResults![0].Error!.Code
            .Should().Be(GeoServicesEditErrorCodes.FeatureLocked);

        var release = await Locks.ReleaseAsync(
            FeatureRefFor(objectId),
            new LockHolder(Holder),
            FeatureLockAccessContext.AuthorizedWrite);
        release.IsSuccess.Should().BeTrue();

        var response = await PostApplyEditsAsync(UpdateNamePayload(objectId, "after-release"));
        var body = await response.Content.ReadAsStringAsync();
        response.Be200Ok();
        Deserialize(body).UpdateResults
            .Should().ContainSingle(edit => edit.Success && edit.ObjectId == objectId, body);
        (await ReadStoredNameAsync(objectId)).Should().Be("after-release");
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits, Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_UnleasedFeatureInTheSameLayer_IsUnaffected()
    {
        // Enforcement is per feature: a lease on one row must not stall the layer.
        var leased = await AddFeatureAsync("leased-row");
        var free = await AddFeatureAsync("free-row");
        await ClaimLeaseAsync(leased);

        var response = await PostApplyEditsAsync(UpdateNamePayload(free, "free-row-edited"));
        var body = await response.Content.ReadAsStringAsync();
        response.Be200Ok();
        Deserialize(body).UpdateResults
            .Should().ContainSingle(edit => edit.Success && edit.ObjectId == free, body);
        (await ReadStoredNameAsync(free)).Should().Be("free-row-edited");
        (await ReadStoredNameAsync(leased)).Should().Be("leased-row");
    }

    private IFeatureLockService Locks => _fixture.GetService<IFeatureLockService>();

    private static FeatureRef FeatureRefFor(long objectId)
        => FeatureRef.Canonical(ServiceId, LayerId, objectId);

    private async Task ClaimLeaseAsync(long objectId)
    {
        var claim = await Locks.ClaimAsync(
            FeatureRefFor(objectId),
            new LockHolder(Holder),
            TimeSpan.FromMinutes(5),
            FeatureLockAccessContext.AuthorizedWrite);

        claim.IsSuccess.Should().BeTrue("the lease the rest of the test depends on must exist");
    }

    private static string UpdateNamePayload(long objectId, string name)
        => """{"updates":[{"attributes":{"objectid":OBJECTID,"name":"NAME"}}]}"""
            .Replace("OBJECTID", objectId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("NAME", name, StringComparison.Ordinal);

    private async Task<long> AddFeatureAsync(string name)
    {
        var response = await PostApplyEditsAsync(
            """{"adds":[{"attributes":{"name":"NAME"},"geometry":{"x":-122.4194,"y":37.7749,"spatialReference":{"wkid":4326}}}]}"""
                .Replace("NAME", name, StringComparison.Ordinal));
        var body = await response.Content.ReadAsStringAsync();
        response.Be200Ok();
        return Deserialize(body).AddResults.Should().ContainSingle(add => add.Success, body).Subject.ObjectId!.Value;
    }

    private Task<string?> ReadStoredNameAsync(long objectId)
        => _fixture.ReadStoredFeatureNameAsync(LayerId, objectId);

    private async Task AssertQueriedNameAsync(long objectId, string expected)
    {
        using var response = await _fixture.Client.GetAsync(
            $"/rest/services/{ServiceId}/FeatureServer/{LayerId}/query?f=json&objectIds={objectId}&outFields=*");
        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("features").EnumerateArray().Single()
            .GetProperty("attributes").GetProperty("name").GetString().Should().Be(expected);
    }

    private async Task<HttpResponseMessage> PostApplyEditsAsync(string json, bool asHolder = false)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/rest/services/{ServiceId}/FeatureServer/{LayerId}/applyEdits")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        if (asHolder)
        {
            request.Headers.Add(FeatureEditLockEnforcement.HolderHeaderName, Holder);
        }

        return await _fixture.Client.SendAsync(request);
    }

    private static ApplyEditsResponse Deserialize(string body)
        => JsonSerializer.Deserialize(body, FeatureServerJsonContext.Default.ApplyEditsResponse)
           ?? throw new InvalidOperationException($"Expected an apply-edits response: {body}");
}
