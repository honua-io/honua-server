// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.ReadOnlyProviders;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
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
public sealed class DefaultVersionIdentityEndpointTests : IAsyncLifetime
{
    private const string Referer = "https://default-identity.test";
    private const string Route = "/rest/services/" + BranchVersioningPublicationFixture.ServiceName + "/VersionManagementServer";
    private readonly WebAppFixture _app = new();
    private string CanonicalServiceId => _app.GetCurrentV2GraphSnapshot().Index.ServicesByName[BranchVersioningPublicationFixture.ServiceName].Metadata.Id;
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
    }

    public Task DisposeAsync() => _app.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer")]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer/versions")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versionInfos")]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}")]
    public async Task RootListInfosAndDetailSharePersistedDefaultIdentityAndTruthfulCapabilities()
    {
        var before = (await Manager.GetDefaultVersionIdentityAsync())!.Value;
        using var get = await _app.Client.GetAsync(Route + "?f=json");
        using var post = await _app.Client.PostAsync(Route, Form());
        var getText = await get.Content.ReadAsStringAsync();
        (await post.Content.ReadAsStringAsync()).Should().Be(getText);
        using var root = JsonDocument.Parse(getText);
        Guid.Parse(root.RootElement.GetProperty("defaultVersionGuid").GetString()!).Should().Be(before.VersionId);
        root.RootElement.GetProperty("defaultVersionName").GetString().Should().Be(before.VersionName);
        root.RootElement.GetProperty("name").GetString().Should().Be("Version Management Server");
        root.RootElement.GetProperty("type").GetString().Should().Be("Map Server Extension");
        root.RootElement.TryGetProperty("currentVersion", out _).Should().BeFalse();
        var capabilities = root.RootElement.GetProperty("capabilities");
        capabilities.ValueKind.Should().Be(JsonValueKind.Object);
        capabilities.GetProperty("supportsMultipleReadersSingleWriterLocking").GetBoolean().Should().BeFalse();
        capabilities.GetProperty("supportsLockInfos").GetBoolean().Should().BeFalse();
        capabilities.GetProperty("supportsPartialPost").GetBoolean().Should().BeFalse();
        using var list = await _app.Client.GetAsync(Route + "/versions?f=json");
        using var infos = await _app.Client.PostAsync(Route + "/versionInfos", Form(("includeHidden", "false")));
        using var detail = await _app.Client.GetAsync(Route + "/versions/" + before.VersionId + "?f=json");
        using var listBody = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        using var infosBody = JsonDocument.Parse(await infos.Content.ReadAsStringAsync());
        using var detailBody = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        var listed = listBody.RootElement.GetProperty("versions").EnumerateArray()
            .Single(version => Guid.Parse(version.GetProperty("versionGuid").GetString()!) == before.VersionId);
        var projected = infosBody.RootElement.GetProperty("versions").EnumerateArray()
            .Single(version => Guid.Parse(version.GetProperty("versionGuid").GetString()!) == before.VersionId);
        listed.GetRawText().Should().Be(detailBody.RootElement.GetRawText());
        projected.GetRawText().Should().Be(detailBody.RootElement.GetRawText());
        infosBody.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        projected.GetProperty("creationDate").GetInt64().Should().Be(before.CreatedAt.ToUnixTimeMilliseconds());
        projected.GetProperty("modifiedDate").GetInt64().Should().Be(before.CreatedAt.ToUnixTimeMilliseconds());
        (await Manager.ListAsync()).Should().NotContain(version => version.VersionId == before.VersionId);
        (await Manager.GetDefaultVersionIdentityAsync()).Should().Be(before);
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer/versions")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versionInfos")]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}")]
    public async Task DefaultCompositionPreservesPrivateBranchVisibilityAndOwnerFilter()
    {
        var branch = await Manager.CreateAsync(new CreateVersionRequest("identity_" + Guid.NewGuid().ToString("N"), "alice", VersionAccess.Private, ServiceId: CanonicalServiceId));
        try
        {
            var bob = await TokenAsync("bob", editor: false);
            using var list = await SendAsync(bob, HttpMethod.Get, Route + "/versions?f=json");
            using var infos = await SendAsync(bob, HttpMethod.Post, Route + "/versionInfos", Form());
            foreach (var response in new[] { list, infos })
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var ids = body.RootElement.GetProperty("versions").EnumerateArray()
                    .Select(version => Guid.Parse(version.GetProperty("versionGuid").GetString()!)).ToArray();
                ids.Should().NotContain(branch.VersionId);
                ids.Should().Contain((await Manager.GetDefaultVersionIdentityAsync())!.Value.VersionId);
            }
            using var hidden = await SendAsync(bob, HttpMethod.Get, Route + "/versions/" + branch.VersionId + "?f=json");
            await hidden.AssertGeoServicesErrorAsync(404);
            using var ownFilter = await SendAsync(bob, HttpMethod.Post, Route + "/versionInfos", Form(("ownerFilter", "alice")));
            using var filtered = JsonDocument.Parse(await ownFilter.Content.ReadAsStringAsync());
            filtered.RootElement.GetProperty("versions").GetArrayLength().Should().Be(0, "owner filters never bypass visibility");
            using var owner = await SendAsync(await TokenAsync("alice", editor: false), HttpMethod.Get, Route + "/versions/" + branch.VersionId + "?f=json");
            using var visible = JsonDocument.Parse(await owner.Content.ReadAsStringAsync());
            visible.RootElement.GetProperty("versionName").GetString().Should().Be("alice." + branch.VersionName);
        }
        finally
        {
            await Manager.DeleteAsync(branch.VersionId);
        }
    }

    [IntegrationTheory]
    [InlineData("delete", 400)]
    [InlineData("alter", 400)]
    [InlineData("reconcile", 400)]
    [InlineData("post", 400)]
    [InlineData("resolveConflicts", 400)]
    [InlineData("startReading", 501)]
    [InlineData("stopReading", 501)]
    [InlineData("startEditing", 501)]
    [InlineData("stopEditing", 501)]
    [Operation(Operations.VersionManagement)]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/delete")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/alter")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/reconcile")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/post")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/resolveConflicts")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/startReading")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/stopReading")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/startEditing")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/stopEditing")]
    public async Task DefaultLifecycleCannotMutateOrAcknowledgeUnimplementedLocks_EvenForDisplayOwner(string operation, int code)
    {
        var before = (await Manager.GetDefaultVersionIdentityAsync())!.Value;
        var branches = await Manager.ListAsync();
        using var admin = await _app.Client.PostAsync(Route + "/versions/" + before.VersionId + "/" + operation, Form());
        await admin.AssertGeoServicesErrorAsync(code);
        using var displayOwner = await SendAsync(await TokenAsync("sde", editor: true), HttpMethod.Post,
            Route + "/versions/" + before.VersionId + "/" + operation, Form());
        await displayOwner.AssertGeoServicesErrorAsync(code);
        using var noWriteRole = await SendAsync(await TokenAsync("sde", editor: false), HttpMethod.Post,
            Route + "/versions/" + before.VersionId + "/" + operation, Form());
        await noWriteRole.AssertGeoServicesErrorAsync(403);
        (await Manager.GetDefaultVersionIdentityAsync()).Should().Be(before);
        (await Manager.ListAsync()).Should().Equal(branches);
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Security)]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer")]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer/versions")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versionInfos")]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}")]
    public async Task DefaultMetadataDoesNotBypassServiceReadPolicy(bool authenticated)
    {
        var id = (await Manager.GetDefaultVersionIdentityAsync())!.Value.VersionId;
        var token = authenticated ? await TokenAsync("outsider", editor: false, reader: false) : null;
        foreach (var suffix in new[] { "", "/versions", "/versions/" + id })
        {
            using var denied = await SendAsync(token, HttpMethod.Get, Route + suffix + "?f=json");
            await denied.AssertGeoServicesErrorAsync(authenticated ? 403 : 499);
        }
        using var infos = await SendAsync(token, HttpMethod.Post, Route + "/versionInfos", Form());
        await infos.AssertGeoServicesErrorAsync(authenticated ? 403 : 499);
    }

    [IntegrationTheory]
    [InlineData("includeHidden", "true", 400)]
    [InlineData("includeHidden", "maybe", 400)]
    [InlineData("nameFilter", "DEFAULT", 501)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versionInfos")]
    public async Task UnsupportedVersionInfosFiltersFailExplicitly(string key, string value, int expectedCode)
    {
        using var response = await _app.Client.PostAsync(Route + "/versionInfos", Form((key, value)));
        await response.AssertGeoServicesErrorAsync(expectedCode);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task DefaultGuidEditsAndQueriesUseTheSameBaseRowsAndNoBranchRecord()
    {
        var identity = (await Manager.GetDefaultVersionIdentityAsync())!.Value;
        var branches = await Manager.ListAsync();
        var layer = "/rest/services/" + BranchVersioningPublicationFixture.ServiceName + "/FeatureServer/0";
        using var add = await _app.Client.PostAsync(layer + "/applyEdits", Form(
            ("gdbVersion", identity.VersionId.ToString()),
            ("adds", """[{"attributes":{"name":"default_identity_base","count":7},"geometry":{"x":0,"y":0,"spatialReference":{"wkid":4326}}}]""")));
        using var added = JsonDocument.Parse(await add.Content.ReadAsStringAsync());
        added.RootElement.GetProperty("addResults")[0].GetProperty("success").GetBoolean().Should().BeTrue();
        var objectId = added.RootElement.GetProperty("addResults")[0].GetProperty("objectId").GetInt64();
        try
        {
            string? first = null;
            foreach (var version in new[] { "", "sde.DEFAULT", "DEFAULT", identity.VersionId.ToString("B") })
            {
                using var query = await _app.Client.GetAsync(layer + "/query?f=json&returnGeometry=false&outFields=*&objectIds="
                    + objectId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "&gdbVersion=" + Uri.EscapeDataString(version));
                var body = await query.Content.ReadAsStringAsync();
                using var result = JsonDocument.Parse(body);
                result.RootElement.GetProperty("features").GetArrayLength().Should().Be(1);
                result.RootElement.GetProperty("features")[0].GetProperty("attributes").GetProperty("name").GetString()
                    .Should().Be("default_identity_base");
                if (first is not null) body.Should().Be(first);
                first = body;
            }
            (await Manager.GetVersionAsync(identity.VersionId)).Should().BeNull();
            (await Manager.ResolveAsync(identity.VersionId.ToString()))!.Value.Should().Be(VersionContext.Default);
            (await Manager.ListAsync()).Should().Equal(branches);
        }
        finally
        {
            using var delete = await _app.Client.PostAsync(layer + "/applyEdits", Form(
                ("gdbVersion", identity.VersionId.ToString()),
                ("deletes", objectId.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            using var deleted = JsonDocument.Parse(await delete.Content.ReadAsStringAsync());
            deleted.RootElement.GetProperty("deleteResults")[0].GetProperty("success").GetBoolean().Should().BeTrue();
        }
    }

    [IntegrationTheory]
    [InlineData("license", 402)]
    [InlineData("provider", 501)]
    [InlineData("experimental", 404)]
    [Operation(Operations.Security)]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer")]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer/versions")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versionInfos")]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}")]
    public async Task DefaultDiscoveryRetainsLicenseProviderAndExperimentalGates(string gate, int expectedCode)
    {
        var identity = (await Manager.GetDefaultVersionIdentityAsync())!.Value;
        var other = new WebAppFixture().WithTestLicense(gate == "license" ? HonuaEdition.Community : HonuaEdition.Enterprise);
        other.ConfigureWebHost(builder => builder.UseSetting("Capabilities:Experimental:versioning.branch:Enabled", gate == "experimental" ? "false" : "true"));
        if (gate == "provider")
        {
            other.ConfigureServices(services =>
            {
                services.RemoveAll<IVersionManager>();
                services.AddSingleton<IVersionManager, NoOpVersionManager>();
            });
        }
        try
        {
            await other.InitializeAsync();
            BranchVersioningPublicationFixture.ConfigureManagedPublications(other);
            foreach (var suffix in new[] { "", "/versions", "/versions/" + identity.VersionId })
            {
                using var denied = await other.Client.GetAsync(Route + suffix + "?f=json");
                await denied.AssertGeoServicesErrorAsync(expectedCode);
            }
            using var infos = await other.Client.PostAsync(Route + "/versionInfos", Form());
            await infos.AssertGeoServicesErrorAsync(expectedCode);
        }
        finally
        {
            await other.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer")]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}")]
    public async Task IndependentlyDisposedAndRecreatedHostsReadTheSamePersistedDescriptor()
    {
        var first = await ReadFromFreshHostAsync();
        var second = await ReadFromFreshHostAsync();
        second.Should().Be(first, "host recreation cannot allocate a new DEFAULT identity or timestamp");

        static async Task<string> ReadFromFreshHostAsync()
        {
            var host = new WebAppFixture().WithTestLicense(HonuaEdition.Enterprise);
            host.ConfigureWebHost(builder => builder.UseSetting("Capabilities:Experimental:versioning.branch:Enabled", "true"));
            try
            {
                await host.InitializeAsync();
                BranchVersioningPublicationFixture.ConfigureManagedPublications(host);
                using var root = await host.Client.GetAsync(Route + "?f=json");
                using var rootBody = JsonDocument.Parse(await root.Content.ReadAsStringAsync());
                var guid = Guid.Parse(rootBody.RootElement.GetProperty("defaultVersionGuid").GetString()!);
                using var detail = await host.Client.GetAsync(Route + "/versions/" + guid + "?f=json");
                var text = await detail.Content.ReadAsStringAsync();
                using var body = JsonDocument.Parse(text);
                Guid.Parse(body.RootElement.GetProperty("versionGuid").GetString()!).Should().Be(guid);
                return text;
            }
            finally
            {
                await host.DisposeAsync();
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer")]
    public async Task DifferentManagedPublicationsInTheSameStoreShareDefaultIdentity()
    {
        var snapshot = _app.GetCurrentV2GraphSnapshot();
        var source = snapshot.Index.ServicesByName[BranchVersioningPublicationFixture.ServiceName];
        const string secondId = "svc-default-identity-second";
        const string secondName = "default-identity-second";
        var publications = snapshot.Graph.Publications.Where(publication => publication.ServiceId == source.Metadata.Id)
            .Select(publication => publication with
            {
                ServiceId = secondId,
                Metadata = publication.Metadata with { Id = "identity-second-" + publication.Metadata.Id }
            }).ToArray();
        var second = source with
        {
            Metadata = source.Metadata with { Id = secondId, Name = secondName },
            PublicationIds = publications.Select(publication => publication.Metadata.Id).ToArray()
        };
        var provider = (TestMetadataV2GraphProvider)_app.GetService<IMetadataV2GraphProvider>();
        provider.SetGraph(snapshot.Graph with
        {
            Revision = snapshot.Graph.Revision + 1,
            Services = snapshot.Graph.Services.Append(second).ToArray(),
            Publications = snapshot.Graph.Publications.Concat(publications).ToArray()
        }, schema: _app.CurrentSchema);
        using var firstResponse = await _app.Client.GetAsync(Route + "?f=json");
        using var secondResponse = await _app.Client.GetAsync("/rest/services/" + secondName + "/VersionManagementServer?f=json");
        using var firstBody = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        using var secondBody = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());
        secondBody.RootElement.GetProperty("defaultVersionGuid").GetString()
            .Should().Be(firstBody.RootElement.GetProperty("defaultVersionGuid").GetString());
        secondBody.RootElement.GetProperty("defaultVersionName").GetString()
            .Should().Be(firstBody.RootElement.GetProperty("defaultVersionName").GetString());
    }

    private async Task<string> TokenAsync(string name, bool editor, bool reader = true)
    {
        var roles = new List<string>();
        if (reader) roles.Add("default-reader");
        if (editor) roles.Add("data-editor:" + BranchVersioningPublicationFixture.ServiceName);
        return (await _app.GetService<IPortalTokenIssuer>().IssueAsync(new PortalTokenIssueRequest(
            name, name, TenantId: null, Roles: roles.ToArray(), PortalTokenClientType.Referer, Referer,
            DateTimeOffset.UtcNow.AddMinutes(30)), CancellationToken.None)).Token;
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
