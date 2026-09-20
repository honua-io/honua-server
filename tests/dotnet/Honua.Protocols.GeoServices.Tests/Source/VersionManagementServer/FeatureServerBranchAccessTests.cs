// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Execution;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.VersionManagementServer;

/// <summary>Actual HTTP branch data access with distinct non-admin editor principals.</summary>
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerBranchAccessTests(FeatureServerBranchAccessFixture fixture, ITestOutputHelper output)
    : IClassFixture<FeatureServerBranchAccessFixture>
{
    private const string Layer = "/rest/services/" + BranchVersioningPublicationFixture.ServiceName + "/FeatureServer/0";
    private const string Service = "/rest/services/" + BranchVersioningPublicationFixture.ServiceName + "/FeatureServer";
    private IVersionManager Manager => fixture.App.GetService<IVersionManager>();

    [IntegrationTheory]
    [InlineData(VersionAccess.Private, "bob", false, "FeatureServer")]
    [InlineData(VersionAccess.Private, "alice", true, "FeatureServer")]
    [InlineData(VersionAccess.Private, "administrator", true, "FeatureServer")]
    [InlineData(VersionAccess.Protected, "bob", true, "FeatureServer")]
    [InlineData(VersionAccess.Public, "bob", true, "FeatureServer")]
    [InlineData(VersionAccess.Private, "bob", false, "MapServer")]
    [InlineData(VersionAccess.Private, "alice", true, "MapServer")]
    [InlineData(VersionAccess.Public, "bob", true, "MapServer")]
    [Operation(Operations.Security)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/{layerId}/query")]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/{layerId}/query")]
    public async Task Reads_RespectPrivateVisibilityAcrossRowsCountIdsAndStatistics(VersionAccess access, string caller, bool allowed, string protocol)
    {
        var marker = "branch_access_" + Guid.NewGuid().ToString("N");
        var version = await Manager.CreateAsync(new CreateVersionRequest(marker, "alice", access));
        try
        {
            var objectId = await SeedBranchAsync(version.VersionId, marker);
            using var scope = new AssertionScope();
            foreach (var mode in new[] { "rows", "count", "ids", "statistics" })
            {
                var values = QueryValues(version.VersionId, marker);
                switch (mode)
                {
                    case "count": values["returnCountOnly"] = "true"; break;
                    case "ids": values["returnIdsOnly"] = "true"; break;
                    case "statistics":
                        values.Remove("outFields");
                        values["outStatistics"] = "[{\"statisticType\":\"count\",\"onStatisticField\":\"objectid\",\"outStatisticFieldName\":\"n\"}]";
                        break;
                }
                var body = await SendAsync(caller, Layer.Replace("FeatureServer", protocol, StringComparison.Ordinal) + "/query", values, mode == "statistics");
                if (!allowed)
                {
                    AssertError(body, 404, "a private branch must not disclose rows, counts, IDs, or statistics to another editor");
                    continue;
                }
                body.TryGetProperty("error", out _).Should().BeFalse(body.ToString());
                if (body.TryGetProperty("error", out _))
                {
                    continue;
                }
                switch (mode)
                {
                    case "count": body.GetProperty("count").GetInt32().Should().Be(1); break;
                    case "ids": body.GetProperty("objectIds")[0].GetInt64().Should().Be(objectId); break;
                    case "statistics": body.GetProperty("features")[0].GetProperty("attributes").GetProperty("n").GetInt64().Should().Be(1); break;
                    default: body.GetProperty("features")[0].GetProperty("attributes").GetProperty("name").GetString().Should().Be(marker); break;
                }
            }
            await AssertDefaultAbsentAsync(marker);
        }
        finally
        {
            (await Manager.DeleteAsync(version.VersionId)).Should().BeTrue();
        }
    }

    [IntegrationTheory]
    [InlineData(VersionAccess.Private, "bob", false)]
    [InlineData(VersionAccess.Protected, "bob", false)]
    [InlineData(VersionAccess.Private, "alice", true)]
    [InlineData(VersionAccess.Protected, "alice", true)]
    [InlineData(VersionAccess.Private, "administrator", true)]
    [InlineData(VersionAccess.Public, "bob", true)]
    [InlineData(VersionAccess.Public, "viewer", false)]
    [Operation(Operations.Security)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/applyEdits")]
    public async Task Writes_RespectPrivateAndProtectedOwnershipAtLayerAndService(VersionAccess access, string caller, bool allowed)
    {
        var marker = "branch_edit_" + Guid.NewGuid().ToString("N");
        // This fixture owns its isolated database. Seed a DEFAULT row before creating
        // the branch so authorization is independent of branch-created-row pre-read bugs.
        var objectId = await SeedDefaultAsync(marker);
        Guid? ownedVersion = null;
        try
        {
            var version = await Manager.CreateAsync(new CreateVersionRequest(marker, "alice", access));
            ownedVersion = version.VersionId;
            version.Owner.Should().Be("alice");
            version.Access.Should().Be(access);
            output.WriteLine("Owned branch {0}, owner {1}, access {2}, baseline object {3}",
                version.VersionId, version.Owner, version.Access, objectId);
            var expectedName = marker;
            using var scope = new AssertionScope();
            foreach (var serviceLevel in new[] { false, true })
            {
                var changed = marker + (serviceLevel ? "_service" : "_layer");
                var updates = JsonSerializer.Serialize(new[] { new { attributes = new { objectid = objectId, name = changed } } });
                var values = new Dictionary<string, string>
                {
                    ["f"] = "json",
                    ["gdbVersion"] = version.VersionId.ToString(),
                    ["rollbackOnFailure"] = "true"
                };
                if (serviceLevel)
                {
                    values["edits"] = "[{\"id\":0,\"updates\":" + updates + "}]";
                }
                else
                {
                    values["updates"] = updates;
                }
                var body = await SendAsync(caller, (serviceLevel ? Service : Layer) + "/applyEdits", values, true);
                if (allowed)
                {
                    body.TryGetProperty("error", out _).Should().BeFalse(body.ToString());
                    if (!body.TryGetProperty("error", out _))
                    {
                        var layerResult = serviceLevel ? body.GetProperty("editResults")[0] : body;
                        layerResult.GetProperty("updateResults")[0].GetProperty("success").GetBoolean().Should().BeTrue();
                        expectedName = changed;
                    }
                }
                else
                {
                    AssertError(body, access == VersionAccess.Private ? 404 : 403,
                        "another authorized editor must not mutate private or protected branch data");
                }
                var readback = await SendAsync("alice", Layer + "/query", new Dictionary<string, string>
                {
                    ["f"] = "json",
                    ["gdbVersion"] = version.VersionId.ToString(),
                    ["objectIds"] = objectId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["outFields"] = "objectid,name",
                    ["returnGeometry"] = "false"
                });
                readback.GetProperty("features")[0].GetProperty("attributes").GetProperty("name").GetString()
                    .Should().Be(expectedName, "the owner readback must prove denied edits did not mutate its branch");
            }
            await AssertDefaultNameAsync(objectId, marker);
        }
        finally
        {
            try
            {
                if (ownedVersion is { } versionId)
                {
                    (await Manager.DeleteAsync(versionId)).Should().BeTrue();
                }
            }
            finally
            {
                var cleanup = await SendAsync("administrator", Layer + "/applyEdits", new Dictionary<string, string>
                {
                    ["f"] = "json",
                    ["deletes"] = objectId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }, true);
                cleanup.GetProperty("deleteResults")[0].GetProperty("success").GetBoolean().Should().BeTrue();
                await AssertDefaultAbsentAsync(marker);
            }
        }
    }

    private async Task<long> SeedDefaultAsync(string marker)
    {
        var body = await SendAsync("administrator", Layer + "/applyEdits", new Dictionary<string, string>
        {
            ["f"] = "json",
            ["adds"] = JsonSerializer.Serialize(new[] { new { attributes = new { name = marker } } })
        }, true);
        body.GetProperty("addResults")[0].GetProperty("success").GetBoolean().Should().BeTrue(body.ToString());
        var objectId = body.GetProperty("addResults")[0].GetProperty("objectId").GetInt64();
        await AssertDefaultNameAsync(objectId, marker);
        return objectId;
    }

    private async Task AssertDefaultNameAsync(long objectId, string expectedName)
    {
        var body = await SendAsync("administrator", Layer + "/query", new Dictionary<string, string>
        {
            ["f"] = "json",
            ["objectIds"] = objectId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["outFields"] = "objectid,name",
            ["returnGeometry"] = "false"
        });
        body.GetProperty("features").GetArrayLength().Should().Be(1);
        body.GetProperty("features")[0].GetProperty("attributes").GetProperty("name").GetString()
            .Should().Be(expectedName, "branch edits must preserve the owned DEFAULT baseline");
    }

    private async Task<long> SeedBranchAsync(Guid version, string marker)
    {
        var body = await SendAsync("alice", Layer + "/applyEdits", new Dictionary<string, string>
        {
            ["f"] = "json",
            ["gdbVersion"] = version.ToString(),
            ["adds"] = JsonSerializer.Serialize(new[] { new { attributes = new { name = marker } } })
        }, true);
        body.TryGetProperty("error", out _).Should().BeFalse(body.ToString());
        body.GetProperty("addResults")[0].GetProperty("success").GetBoolean().Should().BeTrue(body.ToString());
        await AssertDefaultAbsentAsync(marker);
        return body.GetProperty("addResults")[0].GetProperty("objectId").GetInt64();
    }

    private async Task AssertDefaultAbsentAsync(string marker)
    {
        var body = await SendAsync("administrator", Layer + "/query", new Dictionary<string, string>
        {
            ["f"] = "json",
            ["where"] = "name LIKE '" + marker + "%'",
            ["returnCountOnly"] = "true"
        });
        body.GetProperty("count").GetInt32().Should().Be(0, "all owned data changes stay in the branch");
    }

    private static Dictionary<string, string> QueryValues(Guid version, string marker) => new()
    {
        ["f"] = "json",
        ["gdbVersion"] = version.ToString(),
        ["where"] = "name='" + marker + "'",
        ["outFields"] = "objectid,name",
        ["returnGeometry"] = "false"
    };

    private static void AssertError(JsonElement body, int code, string reason)
    {
        var hasError = body.TryGetProperty("error", out var error);
        hasError.Should().BeTrue(reason + "; actual: " + body);
        if (hasError)
        {
            error.GetProperty("code").GetInt32().Should().Be(code, reason);
        }
        body.TryGetProperty("features", out _).Should().BeFalse(reason);
        body.TryGetProperty("updateResults", out _).Should().BeFalse(reason);
        body.TryGetProperty("editResults", out _).Should().BeFalse(reason);
    }

    private async Task<JsonElement> SendAsync(string caller, string path, Dictionary<string, string> values, bool post = false)
    {
        using var content = new FormUrlEncodedContent(values);
        var encoded = await content.ReadAsStringAsync();
        using var request = new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Get, path + (post ? "" : "?" + encoded));
        if (post)
        {
            request.Content = new StringContent(encoded, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
        }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Tokens[caller]);
        request.Headers.Referrer = new Uri(FeatureServerBranchAccessFixture.Referer);
        using var client = fixture.App.CreateClient();
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        output.WriteLine("{0}: {1} {2} HTTP {3}; {4}", caller, request.Method, path, (int)response.StatusCode, body);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "GeoServices success and error envelopes use HTTP200: {0}", body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}

/// <summary>Owns the isolated database and separately issued editor/admin identities for branch access checks.</summary>
public sealed class FeatureServerBranchAccessFixture : IAsyncLifetime
{
    internal const string Referer = "https://branch-feature-access-proof.example/";
    internal WebAppFixture App { get; } = new();
    internal Dictionary<string, string> Tokens { get; } = [];

    public async Task InitializeAsync()
    {
        App.WithTestLicense(HonuaEdition.Enterprise);
        App.ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.UseSetting("Capabilities:Experimental:versioning.branch:Enabled", "true");
        });
        await App.InitializeAsync();
        BranchVersioningPublicationFixture.ConfigureManagedPublications(App);
        App.EnableV2ServiceEditingCapabilities(BranchVersioningPublicationFixture.ServiceName, ["Create", "Update", "Delete"]);
        foreach (var principal in new[] { "alice", "bob", "administrator", "viewer" })
        {
            string[] roles = principal == "administrator" ? ["admin"]
                : principal == "viewer" ? [] : ["data-editor:" + BranchVersioningPublicationFixture.ServiceName];
            Tokens[principal] = (await App.GetService<IPortalTokenIssuer>().IssueAsync(new PortalTokenIssueRequest(
                principal, principal, TenantId: null, Roles: roles, PortalTokenClientType.Referer, Referer,
                DateTimeOffset.UtcNow.AddMinutes(30)), CancellationToken.None)).Token;
        }
    }

    public Task DisposeAsync() => App.DisposeAsync();
}
