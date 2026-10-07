// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.VersionManagementServer;

/// <summary>
/// #4423: HTTP-level authorization proof for the VersionManagementServer lifecycle handlers.
/// <para>
/// Every VMS route is <c>.AllowAnonymous()</c> at the framework level, so the handler-internal
/// ownership policy is the only control. Before this file, <c>VersionAccessPolicyTests</c> proved
/// <c>VersionAccessPolicy.CanManageVersion</c> in isolation and every VMS endpoint test ran
/// under the F1 development-authentication bypass, which returns an <c>admin</c> principal before
/// any credential is read. Under that bypass <c>isAdmin</c> is always <see langword="true"/>, so
/// the non-owner denial branches (BH3-002/003/004, BH6-001) were never executed through
/// <c>HandleDelete</c>, <c>HandleAlter</c>, <c>HandleReconcile</c>, <c>HandlePost</c> or
/// <c>HandleInspectConflicts</c>, and nothing proved those handlers call the policy at all.
/// </para>
/// <para>
/// These tests run with <c>HONUA_DEV_AUTH=false</c> and two genuinely non-admin portal
/// credentials that differ only in principal name. Both hold the service-scoped data-editor role,
/// so both clear the service write gate — the positive control below proves that by having the
/// non-owner successfully create and manage a version of its own. What the non-owner cannot do is
/// touch a version it does not own, and the assertions go past the status: after every denied
/// attempt the owner re-reads the version and finds its name, access, description and status
/// unchanged, i.e. the denial produced no mutation.
/// </para>
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.VersionManagementServer)]
public sealed class VersionManagementServerAuthorizationTests : IAsyncLifetime
{
    private const string ServiceBase =
        "/rest/services/" + WebAppFixture.TestServiceId + "/VersionManagementServer";

    /// <summary>The service-scoped data-editor role (<c>RbacOptions.DataEditorServicePrefix</c>).</summary>
    private const string EditorRole = "data-editor:" + WebAppFixture.TestServiceId;

    private const string Referer = "https://vms-authorization-proof.example/";

    /// <summary>Esri's <c>TokenRequired</c> error code, returned to an unauthenticated caller.</summary>
    private const int EsriTokenRequired = 499;

    private readonly WebAppFixture _fixture = new();

    private string _ownerToken = null!;
    private string _nonOwnerToken = null!;

    public async Task InitializeAsync()
    {
        _fixture.WithTestLicense(HonuaEdition.Enterprise);
        _fixture.ConfigureWebHost(builder =>
        {
            // Displace the F1 development-authentication bypass: without this every request is an
            // admin and the ownership branch under test is unreachable.
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });
        await _fixture.InitializeAsync();

        _ownerToken = await IssueAsync("alice");
        _nonOwnerToken = await IssueAsync("bob");
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    /// <summary>
    /// A non-owner, non-admin, service-write-authorized principal is refused by all five
    /// lifecycle handlers, and the owner's version is unchanged afterwards.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/delete")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/alter")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/reconcile")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/post")]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/inspectConflicts")]
    public async Task LifecycleHandlers_NonOwnerWithServiceWriteAccess_AreDeniedAndMutateNothing()
    {
        // The owner's version, with a description the alter attempt below tries to overwrite.
        var owned = await CreateVersionAsync(_ownerToken, "alice.owned_lifecycle", "owner-description");
        var guid = owned.GetProperty("versionGuid").GetString()!;
        owned.GetProperty("owner").GetString().Should().Be(
            "alice",
            "the create handler stamps the authenticated principal name as the version owner");

        // Positive control: the same non-owner credential is genuinely authorized on this service
        // and on this protocol. It creates and alters a version of its own, so every denial below
        // is attributable to ownership, not to a credential that cannot reach the surface at all.
        var bobsOwn = await CreateVersionAsync(_nonOwnerToken, "bob.owned_control", "bob-description");
        var bobsGuid = bobsOwn.GetProperty("versionGuid").GetString()!;
        using (var control = await PostFormAsync(
            _nonOwnerToken,
            $"{ServiceBase}/versions/{bobsGuid}/alter",
            ("description", "bob-updated"),
            ("f", "json")))
        {
            control.StatusCode.Should().Be(
                HttpStatusCode.OK,
                "the non-owner credential must be able to manage its OWN version: {0}",
                await control.Content.ReadAsStringAsync());
        }

        // ---- the five lifecycle handlers, as the non-owner --------------------------
        using (var delete = await PostFormAsync(
            _nonOwnerToken, $"{ServiceBase}/versions/{guid}/delete", ("f", "json")))
        {
            await AssertDeniedAsync(delete, "delete");
        }

        using (var alter = await PostFormAsync(
            _nonOwnerToken,
            $"{ServiceBase}/versions/{guid}/alter",
            ("description", "hijacked-by-bob"),
            ("versionName", "bob.stolen"),
            ("accessPermission", "public"),
            ("f", "json")))
        {
            await AssertDeniedAsync(alter, "alter");
        }

        using (var reconcile = await PostFormAsync(
            _nonOwnerToken, $"{ServiceBase}/versions/{guid}/reconcile", ("f", "json")))
        {
            await AssertDeniedAsync(reconcile, "reconcile");
        }

        using (var post = await PostFormAsync(
            _nonOwnerToken, $"{ServiceBase}/versions/{guid}/post", ("f", "json")))
        {
            await AssertDeniedAsync(post, "post");
        }

        using (var inspect = await GetAsync(
            _nonOwnerToken, $"{ServiceBase}/versions/{guid}/inspectConflicts?f=json"))
        {
            var body = await inspect.Content.ReadAsStringAsync();
            await AssertDeniedAsync(inspect, "inspectConflicts");

            // inspectConflicts is the read-shaped member of the set: prove it disclosed no
            // conflict projection of a version the caller may not manage.
            body.Should().NotContain("\"conflicts\"");
            body.Should().NotContain("\"hasConflicts\"");
        }

        // ---- nothing was mutated ----------------------------------------------------
        var after = await ReadVersionAsync(_ownerToken, guid);
        after.GetProperty("versionName").GetString().Should().Be(
            owned.GetProperty("versionName").GetString(),
            "a denied alter must not rename the version");
        after.GetProperty("owner").GetString().Should().Be("alice");
        after.GetProperty("access").GetString().Should().Be(
            "private",
            "a denied alter must not widen the access level");
        after.GetProperty("description").GetString().Should().Be(
            "owner-description",
            "a denied alter must not overwrite the description");
        after.GetProperty("status").GetString().Should().Be(
            "active",
            "a denied delete/reconcile/post must leave the version active");
    }

    /// <summary>
    /// A private version is not disclosed to a non-owner by the list or info surfaces, and the
    /// owner still sees it — the visibility half of the same policy.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer/versions")]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}")]
    public async Task VersionVisibility_PrivateVersionOfAnotherOwner_IsNotDisclosed()
    {
        var owned = await CreateVersionAsync(_ownerToken, "alice.private_visibility", "secret-description");
        var guid = owned.GetProperty("versionGuid").GetString()!;

        using var list = await GetAsync(_nonOwnerToken, $"{ServiceBase}/versions?f=json");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var listBody = await list.Content.ReadAsStringAsync();
        listBody.Should().NotContain(
            "alice.private_visibility",
            "a private version must not appear in another principal's version list");
        listBody.Should().NotContain(guid);
        listBody.Should().NotContain("secret-description");

        using var info = await GetAsync(_nonOwnerToken, $"{ServiceBase}/versions/{guid}?f=json");
        var infoBody = await info.Content.ReadAsStringAsync();

        // BH3-002 deliberately answers 404, not 403, so a non-owner cannot confirm the version
        // exists. Either way the record itself must not be in the body.
        await info.AssertGeoServicesErrorAsync((int)HttpStatusCode.NotFound);
        infoBody.Should().NotContain("alice.private_visibility");
        infoBody.Should().NotContain("secret-description");

        // The owner still reads it: the assertions above measure the policy, not an absent row.
        var visible = await ReadVersionAsync(_ownerToken, guid);
        visible.GetProperty("versionName").GetString().Should().Be("alice.private_visibility");
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task FeatureQuery_PrivateVersionOfAnotherOwner_DoesNotExposeBranch(bool useGuid)
    {
        // The version registry outlives each fixture when CI supplies a shared database.
        // Give every theory invocation its own version, including repeated runs.
        var versionName = $"private_query_visibility_{Guid.NewGuid():N}";
        var owned = await CreateVersionAsync(_ownerToken, versionName, "private-query-description");
        var versionGuid = owned.GetProperty("versionGuid").GetString()!;
        BranchVersioningPublicationFixture.ConfigureManagedPublications(_fixture);

        var identity = useGuid ? versionGuid : $"alice.{versionName}";
        var query = $"/rest/services/{BranchVersioningPublicationFixture.ServiceName}/FeatureServer/0/query" +
            $"?where=1%3D1&returnCountOnly=true&gdbVersion={Uri.EscapeDataString(identity)}&f=json";
        using var denied = await GetAsync(_nonOwnerToken, query);
        await denied.AssertGeoServicesErrorAsync((int)HttpStatusCode.NotFound);

        // The same version remains readable by its owner, proving this is a
        // visibility denial rather than a missing branch or broken query route.
        using var allowed = await GetAsync(_ownerToken, query);
        var body = await allowed.Content.ReadAsStringAsync();
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse(body);
        document.RootElement.GetProperty("count").GetInt32().Should().BeGreaterThanOrEqualTo(0);
    }

    /// <summary>
    /// #5036: a read session follows the read rule, not the lifecycle rule. A client starts one on
    /// DEFAULT to open the workspace and on each version it creates or switches to, so a non-owner,
    /// non-admin reader opens and closes read sessions on DEFAULT and on another owner's public
    /// version, while another owner's private version stays undisclosed (404, no acknowledgement).
    /// Edit sessions are not part of that exchange and stay owner-or-admin only.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/startReading")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/stopReading")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/startEditing")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/create")]
    public async Task ReadSessions_FollowVersionVisibility_AndEditSessionsStayOwnerOnly()
    {
        // The GeoServices branch-versioning contract's fixed DEFAULT version GUID.
        const string defaultGuid = "BD3F4817-9A00-41AC-B0CC-58F78DBAE0A1";

        using (var info = await PostFormAsync(
            _nonOwnerToken, $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/versions/{defaultGuid}", ("f", "json")))
        {
            var body = await info.Content.ReadAsStringAsync();
            info.StatusCode.Should().Be(HttpStatusCode.OK, body);
            using var document = JsonDocument.Parse(body);
            document.RootElement.TryGetProperty("error", out _).Should().BeFalse(body);
            document.RootElement.GetProperty("versionName").GetString().Should().Be("sde.DEFAULT");
        }

        foreach (var url in new[]
                 {
                     $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/versions/{defaultGuid}/startReading",
                     $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/versions/{defaultGuid}/stopReading",
                 })
        {
            using var session = await PostFormAsync(
                _nonOwnerToken, url, ("f", "json"), ("sessionId", "{D447A85D-E9BE-498C-892C-F5D89C791D94}"));
            var body = await session.Content.ReadAsStringAsync();
            session.StatusCode.Should().Be(HttpStatusCode.OK, body);
            using var document = JsonDocument.Parse(body);
            document.RootElement.TryGetProperty("error", out _).Should().BeFalse("{0}: {1}", url, body);
            document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue("{0}: {1}", url, body);
        }

        var publicVersion = await CreateVersionAsync(
            _ownerToken, $"alice.public_read_{Guid.NewGuid():N}", "public-read", access: "public");
        var privateVersion = await CreateVersionAsync(
            _ownerToken, $"alice.private_read_{Guid.NewGuid():N}", "private-read");
        var publicGuid = publicVersion.GetProperty("versionGuid").GetString()!;
        var privateGuid = privateVersion.GetProperty("versionGuid").GetString()!;
        foreach (var operation in new[] { "startReading", "stopReading" })
        {
            using var readable = await PostFormAsync(
                _nonOwnerToken,
                $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/versions/{publicGuid}/{operation}",
                ("f", "json"), ("sessionId", "{D447A85D-E9BE-498C-892C-F5D89C791D94}"));
            var readableBody = await readable.Content.ReadAsStringAsync();
            readable.StatusCode.Should().Be(HttpStatusCode.OK, readableBody);
            using var readableDocument = JsonDocument.Parse(readableBody);
            readableDocument.RootElement.TryGetProperty("error", out _).Should().BeFalse("{0}: {1}", operation, readableBody);
            readableDocument.RootElement.GetProperty("success").GetBoolean().Should().BeTrue("{0}: {1}", operation, readableBody);

            using var hidden = await PostFormAsync(
                _nonOwnerToken,
                $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/versions/{privateGuid}/{operation}",
                ("f", "json"));
            var hiddenBody = await hidden.Content.ReadAsStringAsync();
            await hidden.AssertGeoServicesErrorAsync((int)HttpStatusCode.NotFound);
            hiddenBody.Should().NotContain("\"success\":true", "{0} must not acknowledge another owner's private version", operation);
        }

        foreach (var guid in new[] { defaultGuid, publicGuid })
        {
            using var editing = await PostFormAsync(
                _nonOwnerToken,
                $"/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer/versions/{guid}/startEditing",
                ("f", "json"));
            var editingBody = await editing.Content.ReadAsStringAsync();
            await editing.AssertGeoServicesErrorAsync((int)HttpStatusCode.NotFound, (int)HttpStatusCode.Forbidden);
            editingBody.Should().NotContain("\"success\":true", "an edit session on {0} stays owner-or-admin only", guid);
        }
    }

    /// <summary>
    /// With the development bypass off, an unauthenticated caller reaches no VMS lifecycle
    /// operation and learns nothing about the versions that exist.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/create")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/delete")]
    public async Task Anonymous_IsDeniedOnCreateAndDelete_AndTheOwnersVersionSurvives()
    {
        var owned = await CreateVersionAsync(_ownerToken, "alice.anon_target", "anon-target-description");
        var guid = owned.GetProperty("versionGuid").GetString()!;

        using (var create = await PostFormAsync(
            token: null,
            $"{ServiceBase}/create",
            ("versionName", "anonymous.created"),
            ("accessPermission", "public"),
            ("f", "json")))
        {
            // 499 is Esri's TokenRequired code, which this server emits for an unauthenticated
            // GeoServices caller; 401/403 are the equivalents on the non-Esri error shapes. All
            // three are denials — the assertion still rejects any success-shaped body.
            await create.AssertGeoServicesErrorAsync(
                (int)HttpStatusCode.Unauthorized,
                (int)HttpStatusCode.Forbidden,
                EsriTokenRequired);
        }

        using (var delete = await PostFormAsync(
            token: null, $"{ServiceBase}/versions/{guid}/delete", ("f", "json")))
        {
            await delete.AssertGeoServicesErrorAsync(
                (int)HttpStatusCode.Unauthorized,
                (int)HttpStatusCode.Forbidden,
                EsriTokenRequired);
        }

        // No anonymous version was created and the owner's version survived the delete attempt.
        var versions = await ListVersionNamesAsync(_ownerToken);
        versions.Should().NotContain("anonymous.created");
        versions.Should().Contain("alice.anon_target");
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/create")]
    public async Task AnonymousPrivateCreate_MatchesProtectedOperationAuthenticationError_AndWritesNothing()
    {
        var owned = await CreateVersionAsync(_ownerToken, "alice.auth_shape", "auth-shape-control");
        var guid = owned.GetProperty("versionGuid").GetString()!;

        using var create = await PostFormAsync(
            token: null,
            $"{ServiceBase}/create",
            ("versionName", "anonymous.private_refused"),
            ("accessPermission", "private"),
            ("f", "json"));
        using var delete = await PostFormAsync(
            token: null,
            $"{ServiceBase}/versions/{guid}/delete",
            ("f", "json"));

        using var createDocument = JsonDocument.Parse(await create.Content.ReadAsByteArrayAsync());
        using var deleteDocument = JsonDocument.Parse(await delete.Content.ReadAsByteArrayAsync());
        var createError = createDocument.RootElement.GetProperty("error");
        var deleteError = deleteDocument.RootElement.GetProperty("error");
        createError.GetProperty("code").GetInt32().Should().Be(deleteError.GetProperty("code").GetInt32());
        createError.GetProperty("message").GetString().Should().Be(deleteError.GetProperty("message").GetString());
        createError.GetProperty("details")[0].GetString().Should().Be(deleteError.GetProperty("details")[0].GetString());
        await create.AssertGeoServicesErrorAsync(EsriTokenRequired);
        create.Headers.WwwAuthenticate.Should().NotBeEmpty();
        create.Headers.WwwAuthenticate.Select(value => value.ToString()).Should()
            .Equal(delete.Headers.WwwAuthenticate.Select(value => value.ToString()));

        var versions = await ListVersionNamesAsync(_ownerToken);
        versions.Should().NotContain("anonymous.private_refused");
    }

    [IntegrationTheory]
    [InlineData("", "ApiKey")]
    [InlineData("?token=", "Bearer")]
    [Operation(Operations.VersionManagement)]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/create")]
    public async Task AnonymousPrivateCreate_WithAnonymousWritePolicy_PreservesAuthenticationChallenge(
        string query, string expectedScheme)
    {
        var owned = await CreateVersionAsync(_ownerToken, "alice.challenge_control", "challenge-control");
        var guid = owned.GetProperty("versionGuid").GetString()!;
        using var control = await PostFormAsync(
            token: null, $"{ServiceBase}/versions/{guid}/delete{query}", ("f", "json"));
        await control.AssertGeoServicesErrorAsync(EsriTokenRequired);
        control.Headers.WwwAuthenticate.Should().ContainSingle()
            .Which.Scheme.Should().Be(expectedScheme);

        _fixture.UpdateV2ServiceMetadata(WebAppFixture.TestServiceId, accessPolicy: new AccessPolicy
        {
            AllowAnonymous = true,
            AllowAnonymousWrite = true
        });

        // A public create proves the anonymous caller clears both service write gates.
        using var publicCreate = await PostFormAsync(
            token: null, $"{ServiceBase}/create{query}",
            ("versionName", "anonymous.public_challenge_control"), ("accessPermission", "public"), ("f", "json"));
        using var publicDocument = JsonDocument.Parse(await publicCreate.Content.ReadAsByteArrayAsync());
        publicDocument.RootElement.TryGetProperty("versionInfo", out _).Should().BeTrue();

        using var create = await PostFormAsync(
            token: null, $"{ServiceBase}/create{query}",
            ("versionName", "anonymous.private_challenge_refused"), ("accessPermission", "private"), ("f", "json"));
        await create.AssertGeoServicesErrorAsync(EsriTokenRequired);
        create.Headers.WwwAuthenticate.Should().ContainSingle()
            .Which.Scheme.Should().Be(expectedScheme);
        create.Headers.WwwAuthenticate.Select(value => value.ToString()).Should()
            .Equal(control.Headers.WwwAuthenticate.Select(value => value.ToString()));
        using var createDocument = JsonDocument.Parse(await create.Content.ReadAsByteArrayAsync());
        using var controlDocument = JsonDocument.Parse(await control.Content.ReadAsByteArrayAsync());
        createDocument.RootElement.GetProperty("error").GetRawText().Should()
            .Be(controlDocument.RootElement.GetProperty("error").GetRawText());

        var versions = await ListVersionNamesAsync(_ownerToken);
        versions.Should().Contain("anonymous.public_challenge_control");
        versions.Should().Contain("alice.challenge_control");
        versions.Should().NotContain("anonymous.private_challenge_refused");
    }

    private static async Task AssertDeniedAsync(HttpResponseMessage response, string operation)
    {
        var body = await response.Content.ReadAsStringAsync();
        await response.AssertGeoServicesErrorAsync((int)HttpStatusCode.Forbidden);

        // A denial must not carry a success-shaped acknowledgement for the operation.
        body.Should().NotContain(
            "\"success\":true",
            "the {0} handler must not acknowledge a denied lifecycle operation",
            operation);
    }

    private async Task<string> IssueAsync(string principalId)
        => (await _fixture.GetService<IPortalTokenIssuer>().IssueAsync(
            new PortalTokenIssueRequest(
                principalId,
                principalId,
                TenantId: null,
                Roles: [EditorRole],
                PortalTokenClientType.Referer,
                Referer,
                DateTimeOffset.UtcNow.AddMinutes(30)),
            CancellationToken.None)).Token;

    private async Task<JsonElement> CreateVersionAsync(
        string token, string versionName, string description, string access = "private")
    {
        using var response = await PostFormAsync(
            token,
            $"{ServiceBase}/create",
            ("versionName", versionName),
            ("accessPermission", access),
            ("description", description),
            ("f", "json"));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "create should succeed; body: {0}", body);

        using var document = JsonDocument.Parse(body);
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse(
            "create should succeed without a GeoServices error; body: {0}", body);
        return document.RootElement.GetProperty("versionInfo").Clone();
    }

    private async Task<JsonElement> ReadVersionAsync(string token, string guid)
    {
        using var response = await GetAsync(token, $"{ServiceBase}/versions/{guid}?f=json");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the owner must still read its version; body: {0}", body);

        // versionInfo returns the VersionInfo shape at the document root.
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private async Task<IReadOnlyList<string>> ListVersionNamesAsync(string token)
    {
        using var response = await GetAsync(token, $"{ServiceBase}/versions?f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("versions")
            .EnumerateArray()
            .Select(version => version.GetProperty("versionName").GetString() ?? string.Empty)
            .ToArray();
    }

    private async Task<HttpResponseMessage> GetAsync(string? token, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendAsync(request, token);
    }

    private async Task<HttpResponseMessage> PostFormAsync(
        string? token,
        string url,
        params (string Key, string Value)[] fields)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(
                fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value))),
        };
        return await SendAsync(request, token);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string? token)
    {
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Referrer = new Uri(Referer);
        }

        // A dedicated client per request: the fixture's shared client carries an admin X-API-Key,
        // which would satisfy every request under test and make the denials unobservable.
        using var client = _fixture.CreateClient();
        return await client.SendAsync(request);
    }
}
