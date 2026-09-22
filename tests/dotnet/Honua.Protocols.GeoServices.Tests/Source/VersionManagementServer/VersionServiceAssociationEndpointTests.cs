// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.ReadOnlyProviders;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.VersionManagementServer;

[Collection("Database.GeoServicesCatalog")]
[Protocol(TestProtocols.VersionManagementServer)]
public sealed class VersionServiceAssociationEndpointTests : IAsyncLifetime
{
    private const string SecondId = "svc-version-scope-b";
    private const string SecondName = "version-scope-b";
    private string FirstId => _app.GetCurrentV2GraphSnapshot().Index.ServicesByName[BranchVersioningPublicationFixture.ServiceName].Metadata.Id;
    private const string Referer = "https://default-identity.test";
    private const string Route = "/rest/services/" + BranchVersioningPublicationFixture.ServiceName + "/VersionManagementServer";
    private readonly WebAppFixture _app = new();
    private IVersionManager Manager => _app.GetService<IVersionManager>();

    public async Task InitializeAsync()
    {
        _app.WithTestLicense(HonuaEdition.Enterprise);
        _app.ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.UseSetting("Capabilities:Experimental:versioning.branch:Enabled", "true");
        });
        await _app.InitializeAsync();
        BranchVersioningPublicationFixture.ConfigureManagedPublications(_app);
        _app.EnableV2ServiceEditingCapabilities(BranchVersioningPublicationFixture.ServiceName, ["Create", "Update", "Delete"]);
        var snapshot = _app.GetCurrentV2GraphSnapshot();
        var graph = snapshot.Graph;
        var provider = (TestMetadataV2GraphProvider)_app.GetService<IMetadataV2GraphProvider>();
        provider.SetGraph(graph with
        {
            Revision = graph.Revision + 1,
            Services = graph.Services.Select(service => service.Metadata.Name == BranchVersioningPublicationFixture.ServiceName
                ? service with { AccessPolicy = new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["default-reader"] } }
                : service).ToArray()
        }, schema: _app.CurrentSchema);
        var current = _app.GetCurrentV2GraphSnapshot();
        var first = current.Index.ServicesByName[BranchVersioningPublicationFixture.ServiceName];
        var publications = current.Graph.Publications.Where(publication => publication.ServiceId == first.Metadata.Id)
            .Select(publication => publication with
            {
                ServiceId = SecondId,
                Metadata = publication.Metadata with { Id = "scope-b-" + publication.Metadata.Id }
            }).ToArray();
        var second = first with
        {
            Metadata = first.Metadata with { Id = SecondId, Name = SecondName },
            PublicationIds = publications.Select(publication => publication.Metadata.Id).ToArray()
        };
        provider.SetGraph(current.Graph with
        {
            Revision = current.Graph.Revision + 1,
            Services = current.Graph.Services.Append(second).ToArray(),
            Publications = current.Graph.Publications.Concat(publications).ToArray()
        }, schema: _app.CurrentSchema);
    }

    public Task DisposeAsync() => _app.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.VersionManagement)]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/create")]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versionInfos")]
    public async Task ServiceCreatedBranch_UsesCanonicalIdentityAndSameServiceNameGuidRoundtrip(bool requestByServiceId)
    {
        var service = requestByServiceId ? FirstId : BranchVersioningPublicationFixture.ServiceName;
        var name = "scope_" + Guid.NewGuid().ToString("N");
        using var create = await _app.Client.PostAsync(Vms(service) + "/create", Form(("versionName", name), ("accessPermission", "public")));
        using var body = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var info = body.RootElement.GetProperty("versionInfo");
        var id = Guid.Parse(info.GetProperty("versionGuid").GetString()!);
        var qualified = info.GetProperty("versionName").GetString()!;
        try
        {
            var descriptor = (await Manager.GetVersionAsync(id))!.Value;
            descriptor.ServiceId.Should().Be(FirstId);
            descriptor.VersionName.Should().Be(name);
            foreach (var alias in new[] { FirstId, BranchVersioningPublicationFixture.ServiceName })
            {
                using var detail = await _app.Client.GetAsync(Vms(alias) + "/versions/" + id + "?f=json");
                using var parsed = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
                parsed.RootElement.GetProperty("versionName").GetString().Should().Be(qualified);
                using var queryName = await _app.Client.GetAsync(Feature(alias) + "/query?f=json&where=1%3D0&gdbVersion=" + Uri.EscapeDataString(qualified));
                using var queryGuid = await _app.Client.GetAsync(Feature(alias) + "/query?f=json&where=1%3D0&gdbVersion=" + id);
                using var namedBody = JsonDocument.Parse(await queryName.Content.ReadAsStringAsync());
                using var guidBody = JsonDocument.Parse(await queryGuid.Content.ReadAsStringAsync());
                namedBody.RootElement.GetProperty("features").GetArrayLength().Should().Be(0);
                guidBody.RootElement.GetProperty("features").GetArrayLength().Should().Be(0);
            }
            using var other = await _app.Client.PostAsync(Vms(SecondName) + "/versionInfos", Form());
            using var otherBody = JsonDocument.Parse(await other.Content.ReadAsStringAsync());
            otherBody.RootElement.GetProperty("versions").EnumerateArray().Should().NotContain(item =>
                Guid.Parse(item.GetProperty("versionGuid").GetString()!) == id);
            (await Manager.ListAsync()).Should().Contain(version => version.VersionId == id);
        }
        finally { await Manager.DeleteAsync(id); }
    }

    [Theory]
    [InlineData("query", false)]
    [InlineData("query", true)]
    [InlineData("count", false)]
    [InlineData("ids", false)]
    [InlineData("statistics", false)]
    [InlineData("edit", false)]
    [InlineData("detail", false)]
    [InlineData("delete", false)]
    [InlineData("alter", false)]
    [InlineData("reconcile", false)]
    [InlineData("post", false)]
    [InlineData("inspectConflicts", false)]
    [Operation(Operations.VersionManagement)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/delete")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/alter")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/reconcile")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/post")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/inspectConflicts")]
    public async Task PublicBranch_CannotBeUsedThroughAnotherAuthorizedService(string operation, bool byName)
    {
        var branch = await Manager.CreateAsync(new CreateVersionRequest("scope_" + Guid.NewGuid().ToString("N"),
            "admin", VersionAccess.Public, ServiceId: FirstId));
        var identity = byName ? branch.Owner + "." + branch.VersionName : branch.VersionId.ToString();
        var baseline = await Manager.GetDefaultVersionIdentityAsync();
        try
        {
            HttpResponseMessage response;
            if (operation is "query" or "count" or "ids" or "statistics")
            {
                var extra = operation switch
                {
                    "count" => "&returnCountOnly=true",
                    "ids" => "&returnIdsOnly=true",
                    "statistics" => "&outStatistics=" + Uri.EscapeDataString("[{\"statisticType\":\"count\",\"onStatisticField\":\"objectid\",\"outStatisticFieldName\":\"n\"}]"),
                    _ => ""
                };
                response = await _app.Client.GetAsync(Feature(SecondName) + "/query?f=json&where=1%3D1&gdbVersion=" + Uri.EscapeDataString(identity) + extra);
            }
            else if (operation == "edit")
            {
                response = await _app.Client.PostAsync(Feature(SecondName) + "/applyEdits", Form(("gdbVersion", identity),
                    ("adds", "[{\"attributes\":{\"name\":\"must-not-write\"}}]")));
            }
            else if (operation == "detail")
            {
                response = await _app.Client.GetAsync(Vms(SecondName) + "/versions/" + branch.VersionId + "?f=json");
            }
            else
            {
                response = await _app.Client.PostAsync(Vms(SecondName) + "/versions/" + branch.VersionId + "/" + operation, Form(("versionName", "must-not-rename")));
            }
            using (response)
            using (var parsed = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
            {
                parsed.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(404);
            }
            (await Manager.GetVersionAsync(branch.VersionId)).Should().Be(branch);
            (await Manager.GetDefaultVersionIdentityAsync()).Should().Be(baseline);
        }
        finally { await Manager.DeleteAsync(branch.VersionId); }
    }

    [Theory]
    [InlineData("alice", 0)]
    [InlineData("bob", 403)]
    [InlineData("admin", 0)]
    [InlineData("anonymous", 499)]
    [Operation(Operations.VersionManagement)]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/adoptService")]
    public async Task LegacyAdoption_RequiresOwnerOrAdminAndPreservesDescriptor(string principal, int errorCode)
    {
        var legacy = await Manager.CreateAsync(new CreateVersionRequest("legacy_" + Guid.NewGuid().ToString("N"), "alice", VersionAccess.Public));
        try
        {
            legacy.ServiceId.Should().BeNull();
            using var before = await _app.Client.GetAsync(Route + "/versions/" + legacy.VersionId + "?f=json");
            using var beforeBody = JsonDocument.Parse(await before.Content.ReadAsStringAsync());
            beforeBody.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(404);
            var path = Route + "/versions/" + legacy.VersionId + "/adoptService";
            using var response = principal == "admin"
                ? await _app.Client.PostAsync(path, Form())
                : await SendAsync(principal == "anonymous" ? null : await TokenAsync(principal, editor: true), HttpMethod.Post, path, Form());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (errorCode == 0)
            {
                body.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
                (await Manager.GetVersionAsync(legacy.VersionId)).Should().Be(legacy with { ServiceId = FirstId });
                using var reassign = await _app.Client.PostAsync(Vms(SecondName) + "/versions/" + legacy.VersionId + "/adoptService", Form());
                using var reassigned = JsonDocument.Parse(await reassign.Content.ReadAsStringAsync());
                reassigned.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(409);
                (await Manager.GetVersionAsync(legacy.VersionId))!.Value.ServiceId.Should().Be(FirstId);
            }
            else
            {
                body.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(errorCode);
                (await Manager.GetVersionAsync(legacy.VersionId)).Should().Be(legacy);
            }
        }
        finally { await Manager.DeleteAsync(legacy.VersionId); }
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/adoptService")]
    public async Task Adoption_WithExistingDeltaRejectsUnpermittedLayer_ThenPreservesOwnedDeltaOnAuthorizedAdoption()
    {
        var legacy = await Manager.CreateAsync(new CreateVersionRequest("delta_" + Guid.NewGuid().ToString("N"), "alice", VersionAccess.Private));
        var snapshot = _app.GetCurrentV2GraphSnapshot();
        var layer = snapshot.Index.PublicationsByService[FirstId]
            .Select(snapshot.ResolveStorageLayerId).First(value => value.HasValue)!.Value;
        var association = (IVersionServiceAssociationManager)Manager;
        try
        {
            await using (var connection = await _app.GetService<IAdoNetDatabaseConnectionProvider>().OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO honua.version_edits(version_id, layer_id, objectid, operation, attributes) VALUES (@id, @layer, 987654321, 1, '{\"name\":\"adoption-preserves-delta\"}'::jsonb)";
                var id = command.CreateParameter(); id.ParameterName = "id"; id.Value = legacy.VersionId;
                var layerParameter = command.CreateParameter(); layerParameter.ParameterName = "layer"; layerParameter.Value = layer;
                command.Parameters.Add(id); command.Parameters.Add(layerParameter);
                await command.ExecuteNonQueryAsync();
            }
            (await association.AssociateLegacyVersionAsync(legacy.VersionId, FirstId, "alice", []))
                .Should().Be(VersionServiceAssociationResult.Conflict);
            (await Manager.GetVersionAsync(legacy.VersionId))!.Value.ServiceId.Should().BeNull();
            using var response = await SendAsync(await TokenAsync("alice", editor: true), HttpMethod.Post,
                Route + "/versions/" + legacy.VersionId + "/adoptService", Form());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            (await Manager.GetVersionAsync(legacy.VersionId))!.Value.ServiceId.Should().Be(FirstId);
            await using var checkConnection = await _app.GetService<IAdoNetDatabaseConnectionProvider>().OpenConnectionAsync();
            await using var check = checkConnection.CreateCommand();
            check.CommandText = "SELECT attributes->>'name' FROM honua.version_edits WHERE version_id=@id AND objectid=987654321";
            var checkId = check.CreateParameter(); checkId.ParameterName = "id"; checkId.Value = legacy.VersionId; check.Parameters.Add(checkId);
            (await check.ExecuteScalarAsync()).Should().Be("adoption-preserves-delta");
        }
        finally { await Manager.DeleteAsync(legacy.VersionId); }
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/adoptService")]
    public async Task Adoption_ContentionReturnsConflictWithoutClaimingSuccess()
    {
        var branch = await Manager.CreateAsync(new CreateVersionRequest("locked_" + Guid.NewGuid().ToString("N"), "alice", VersionAccess.Public));
        try
        {
            // Program registers the manager with schemaName:null, giving this canonical lock scope.
            await using var held = await _app.GetService<IVersionLock>().TryAcquireAsync(
                "honua.versioning", branch.VersionId, TimeSpan.FromMinutes(1), CancellationToken.None);
            held.Should().NotBeNull();
            using var response = await SendAsync(await TokenAsync("alice", editor: true), HttpMethod.Post,
                Route + "/versions/" + branch.VersionId + "/adoptService", Form());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(409);
            body.RootElement.TryGetProperty("success", out _).Should().BeFalse();
            (await Manager.GetVersionAsync(branch.VersionId)).Should().Be(branch);
        }
        finally { await Manager.DeleteAsync(branch.VersionId); }
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/adoptService")]
    public async Task Adoption_ServiceEditorCannotClaimDeltaInResourceDeniedLayer_WithAnotherEligibleLayer()
    {
        var snapshot = _app.GetCurrentV2GraphSnapshot();
        var publications = snapshot.Index.PublicationsByService[FirstId]
            .Where(publication => snapshot.ResolveStorageLayerId(publication).HasValue)
            .DistinctBy(publication => snapshot.ResolveStorageLayerId(publication)).ToArray();
        publications.Length.Should().BeGreaterThanOrEqualTo(2);
        var denied = snapshot.ResolveResource(publications[0])!;
        var deniedLayer = snapshot.ResolveStorageLayerId(publications[0])!.Value;
        var eligible = snapshot.ResolveResource(publications[1])!;
        var provider = (TestMetadataV2GraphProvider)_app.GetService<IMetadataV2GraphProvider>();
        provider.SetGraph(snapshot.Graph with
        {
            Revision = snapshot.Graph.Revision + 1,
            Resources = snapshot.Graph.Resources.Select(resource => resource.Metadata.Id == denied.Metadata.Id
                ? resource with { AccessPolicy = new AccessPolicy { AllowedRoles = ["default-reader"], AllowedWriteRoles = ["unassigned-owner-role"] } }
                : resource.Metadata.Id == eligible.Metadata.Id
                    ? resource with { AccessPolicy = new AccessPolicy { AllowedRoles = ["default-reader"], AllowedWriteRoles = ["default-reader"] } }
                    : resource).ToArray()
        }, schema: _app.CurrentSchema);
        var deltaBranch = await Manager.CreateAsync(new CreateVersionRequest("acl_delta_" + Guid.NewGuid().ToString("N"), "alice", VersionAccess.Private));
        var emptyControl = await Manager.CreateAsync(new CreateVersionRequest("acl_empty_" + Guid.NewGuid().ToString("N"), "alice", VersionAccess.Private));
        try
        {
            await using (var connection = await _app.GetService<IAdoNetDatabaseConnectionProvider>().OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO honua.version_edits(version_id, layer_id, objectid, operation, attributes) VALUES (@id, @layer, 987654322, 1, '{\"name\":\"denied-layer-delta\"}'::jsonb)";
                var id = command.CreateParameter(); id.ParameterName = "id"; id.Value = deltaBranch.VersionId;
                var layer = command.CreateParameter(); layer.ParameterName = "layer"; layer.Value = deniedLayer;
                command.Parameters.Add(id); command.Parameters.Add(layer);
                await command.ExecuteNonQueryAsync();
            }
            var before = await Manager.GetVersionAsync(deltaBranch.VersionId);
            var token = await TokenAsync("alice", editor: true);
            using var positive = await SendAsync(token, HttpMethod.Post, Route + "/versions/" + emptyControl.VersionId + "/adoptService", Form());
            using var positiveBody = JsonDocument.Parse(await positive.Content.ReadAsStringAsync());
            positiveBody.RootElement.GetProperty("success").GetBoolean().Should().BeTrue("another resource remains eligible after ACL filtering");
            using var deniedResponse = await SendAsync(token, HttpMethod.Post, Route + "/versions/" + deltaBranch.VersionId + "/adoptService", Form());
            using var deniedBody = JsonDocument.Parse(await deniedResponse.Content.ReadAsStringAsync());
            deniedBody.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(409);
            deniedBody.RootElement.TryGetProperty("success", out _).Should().BeFalse();
            (await Manager.GetVersionAsync(deltaBranch.VersionId)).Should().Be(before);
            before!.Value.ServiceId.Should().BeNull();
            await using var checkConnection = await _app.GetService<IAdoNetDatabaseConnectionProvider>().OpenConnectionAsync();
            await using var check = checkConnection.CreateCommand();
            check.CommandText = "SELECT attributes->>'name' FROM honua.version_edits WHERE version_id=@id AND objectid=987654322";
            var checkId = check.CreateParameter(); checkId.ParameterName = "id"; checkId.Value = deltaBranch.VersionId; check.Parameters.Add(checkId);
            (await check.ExecuteScalarAsync()).Should().Be("denied-layer-delta");
        }
        finally
        {
            await Manager.DeleteAsync(deltaBranch.VersionId);
            await Manager.DeleteAsync(emptyControl.VersionId);
        }
    }

    private static string Vms(string service) => "/rest/services/" + service + "/VersionManagementServer";
    private static string Feature(string service) => "/rest/services/" + service + "/FeatureServer/0";

    private async Task<string> TokenAsync(string name, bool editor, bool reader = true)
    {
        var roles = new List<string>();
        if (reader) roles.Add("default-reader");
        if (editor)
        {
            roles.Add("data-editor:" + BranchVersioningPublicationFixture.ServiceName);
            roles.Add("data-editor:" + SecondName);
        }
        return (await _app.GetService<IPortalTokenIssuer>().IssueAsync(new PortalTokenIssueRequest(
            name, name, TenantId: null, Roles: roles.ToArray(), PortalTokenClientType.Referer, Referer,
            DateTimeOffset.UtcNow.AddMinutes(30)), CancellationToken.None)).Token;
    }

    [Theory]
    [InlineData("alice", false, 0)]
    [InlineData("bob", false, 404)]
    [InlineData("admin", false, 0)]
    [InlineData("admin", true, 404)]
    [Operation(Operations.VersionManagement)]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/jobs/{jobId}")]
    public async Task JobStatus_RequiresCurrentBranchServiceAndPrivateVisibility(string principal, bool otherService, int errorCode)
    {
        var branch = await Manager.CreateAsync(new CreateVersionRequest("job_scope_" + Guid.NewGuid().ToString("N"),
            "alice", VersionAccess.Private, ServiceId: FirstId));
        var job = new VersionJob(Guid.NewGuid(), FirstId, branch.VersionId, VersionJobKind.Reconcile,
            VersionJobStatus.Succeeded, VersionReconcilePolicy.None, DateTimeOffset.UtcNow);
        await _app.GetService<IVersionJobStore>().SaveAsync(job);
        try
        {
            var path = Vms(otherService ? SecondName : BranchVersioningPublicationFixture.ServiceName)
                + "/versions/" + branch.VersionId + "/jobs/" + job.JobId + "?f=json";
            using var response = principal == "admin"
                ? await _app.Client.GetAsync(path)
                : await SendAsync(await TokenAsync(principal, editor: false), HttpMethod.Get, path);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (errorCode == 0)
            {
                Guid.Parse(body.RootElement.GetProperty("jobId").GetString()!).Should().Be(job.JobId);
            }
            else
            {
                body.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(errorCode);
                body.RootElement.TryGetProperty("jobId", out _).Should().BeFalse();
            }
            (await Manager.GetVersionAsync(branch.VersionId)).Should().Be(branch);
        }
        finally { await Manager.DeleteAsync(branch.VersionId); }
    }

    private async Task<HttpResponseMessage> SendAsync(string? token, HttpMethod method, string route, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, route) { Content = content };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Referrer = new Uri(Referer);
        }
        using var client = _app.CreateClient();
        return await client.SendAsync(request);
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] values)
        => new(values.Append((Key: "f", Value: "json")).Select(value => new KeyValuePair<string, string>(value.Key, value.Value)));
}
