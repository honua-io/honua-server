// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Security;

/// <summary>
/// WFS 2.0 is a GA surface with transactional write support, and its per-operation write
/// authorization runs in <c>Wfs20Handler.Transaction.ValidateTransactionLayerWriteAccessAsync</c>.
/// Until now that call was proven only by <c>WfsTransactionWriteAuthorizationSeamTests</c>,
/// which invokes <c>ServiceDataEditorAuthorization.RequireResourceDataEditorAsync</c> against a
/// hand-built <c>DefaultHttpContext</c>: deleting the call from the dispatcher left the whole
/// suite green. These tests drive the dispatcher over HTTP against a real PostGIS store, and
/// read the stored row back out of Postgres — around every server read path — so a refusal that
/// arrives after the edit committed fails.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Wfs20)]
public sealed class WfsTransactionAuthorizationHttpTests : IAsyncLifetime
{
    private const string DeniedPrincipal = "wfs-denied-user";
    private const string GrantedPrincipal = "wfs-granted-user";

    private readonly WebAppFixture _fixture = OgcClassicAuthorizationFixture.Create();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "Transaction")]
    public async Task Wfs_Transaction_InsertOnlyGrantee_IsDeniedUpdateOverHttp()
    {
        var featureId = await _fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, "WFS Authz Update Target");
        using var client = _fixture.CreateClientAs(DeniedPrincipal, OgcClassicAuthorizationFixture.InsertOnlyRole);

        var response = await PostTransactionAsync(client, UpdateNameTransaction(featureId, "WFS Authz Update Applied"));

        await AssertRefusedAsync(response);
        (await _fixture.ReadStoredFeatureNameAsync(WebAppFixture.TestLayerId, featureId))
            .Should().Be("WFS Authz Update Target", "a refused Update must not have reached the store");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "Transaction")]
    public async Task Wfs_Transaction_InsertOnlyGrantee_IsDeniedReplaceOverHttp()
    {
        // Replace maps to the Update operation in the dispatcher, so an insert-only grant
        // must not authorize it either.
        var featureId = await _fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, "WFS Authz Replace Target");
        using var client = _fixture.CreateClientAs(DeniedPrincipal, OgcClassicAuthorizationFixture.InsertOnlyRole);

        var response = await PostTransactionAsync(client, ReplaceNameTransaction(featureId, "WFS Authz Replace Applied"));

        await AssertRefusedAsync(response);
        (await _fixture.ReadStoredFeatureNameAsync(WebAppFixture.TestLayerId, featureId))
            .Should().Be("WFS Authz Replace Target");
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "Transaction")]
    public async Task Wfs_Transaction_InsertOnlyGrantee_IsDeniedDeleteOverHttp()
    {
        var featureId = await _fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, "WFS Authz Delete Target");
        using var client = _fixture.CreateClientAs(DeniedPrincipal, OgcClassicAuthorizationFixture.InsertOnlyRole);

        var response = await PostTransactionAsync(client, DeleteTransaction(featureId));

        await AssertRefusedAsync(response);
        (await _fixture.ReadStoredFeatureNameAsync(WebAppFixture.TestLayerId, featureId))
            .Should().Be("WFS Authz Delete Target", "a refused Delete must leave the row in place");
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "Transaction")]
    public async Task Wfs_Transaction_InsertGrantee_InsertsOverHttp()
    {
        // The positive control for the three denials above: the same principal, the same
        // route, the one operation it holds. Without it, a dispatcher broken for every
        // caller would satisfy the denial tests.
        const string name = "WFS Authz Insert Applied";
        using var client = _fixture.CreateClientAs(GrantedPrincipal, OgcClassicAuthorizationFixture.InsertOnlyRole);

        var response = await PostTransactionAsync(client, InsertNameTransaction(name));

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("<wfs:totalInserted>1</wfs:totalInserted>");
        (await _fixture.CountStoredFeaturesByNameAsync(WebAppFixture.TestLayerId, name)).Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "Transaction")]
    public async Task Wfs_Transaction_UpdateGrantee_UpdatesOverHttp()
    {
        var featureId = await _fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, "WFS Authz Granted Update Target");
        using var client = _fixture.CreateClientAs(GrantedPrincipal, OgcClassicAuthorizationFixture.UpdateOnlyRole);

        var response = await PostTransactionAsync(client, UpdateNameTransaction(featureId, "WFS Authz Granted Update Applied"));

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("<wfs:totalUpdated>1</wfs:totalUpdated>");
        (await _fixture.ReadStoredFeatureNameAsync(WebAppFixture.TestLayerId, featureId))
            .Should().Be("WFS Authz Granted Update Applied");
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "Transaction")]
    public async Task Wfs_Transaction_DeleteGrantee_DeletesOverHttp()
    {
        var featureId = await _fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, "WFS Authz Granted Delete Target");
        using var client = _fixture.CreateClientAs(GrantedPrincipal, OgcClassicAuthorizationFixture.DeleteOnlyRole);

        var response = await PostTransactionAsync(client, DeleteTransaction(featureId));

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("<wfs:totalDeleted>1</wfs:totalDeleted>");
        (await _fixture.ReadStoredFeatureNameAsync(WebAppFixture.TestLayerId, featureId))
            .Should().BeNull("the authorized Delete must actually remove the row");
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "Transaction")]
    public async Task Wfs_Transaction_UpdateOnlyGrantee_IsDeniedDeleteOverHttp()
    {
        // The narrowing runs in both directions: an update grant is not a delete grant.
        var featureId = await _fixture.InsertFeatureAsync(WebAppFixture.TestLayerId, "WFS Authz Update Grant Delete Target");
        using var client = _fixture.CreateClientAs(DeniedPrincipal, OgcClassicAuthorizationFixture.UpdateOnlyRole);

        var response = await PostTransactionAsync(client, DeleteTransaction(featureId));

        await AssertRefusedAsync(response);
        (await _fixture.ReadStoredFeatureNameAsync(WebAppFixture.TestLayerId, featureId))
            .Should().Be("WFS Authz Update Grant Delete Target");
    }

    [IntegrationTest]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task Wfs_GetFeature_PrincipalWithoutReadAccess_ReturnsNoMembers()
    {
        // Read access is restricted to a role the requesting principal does not hold. The
        // seam test for this path asserted a boolean from AccessPolicyHelpers; here the
        // response document itself must carry no feature.
        _fixture.UpdateV2ResourceMetadata(
            WebAppFixture.TestLayerId,
            accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = [ReaderRole] });

        using var client = _fixture.CreateClientAs(DeniedPrincipal, OgcClassicAuthorizationFixture.InsertOnlyRole);
        var response = await client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES=test_layer");

        var body = await response.Content.ReadAsStringAsync();
        CountFeatureMembers(body).Should().Be(0, body);
        body.Should().NotContain("Test Feature");
        body.Should().NotContain("A test feature for integration tests");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task Wfs_GetFeature_PrincipalWithReadAccess_ReturnsSeededMembers()
    {
        _fixture.UpdateV2ResourceMetadata(
            WebAppFixture.TestLayerId,
            accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = [ReaderRole] });

        using var client = _fixture.CreateClientAs(GrantedPrincipal, ReaderRole);
        var response = await client.GetAsync(
            "/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES=test_layer");

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        CountFeatureMembers(body).Should().BeGreaterThan(0, body);
        body.Should().Contain("Test Feature");
    }

    [IntegrationTest]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetCapabilities")]
    public async Task Wfs_GetCapabilities_PrincipalWithoutReadAccess_OmitsLayer()
    {
        _fixture.UpdateV2ResourceMetadata(
            WebAppFixture.TestLayerId,
            accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = [ReaderRole] });

        using var deniedClient = _fixture.CreateClientAs(DeniedPrincipal, OgcClassicAuthorizationFixture.InsertOnlyRole);
        var deniedResponse = await deniedClient.GetAsync("/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=2.0.0");
        var deniedBody = await deniedResponse.Content.ReadAsStringAsync();
        deniedBody.Should().NotContain("test_layer", deniedBody);

        // Paired grant: the same document does advertise the type to a reader, so the
        // omission above is authorization and not an empty capabilities document.
        using var grantedClient = _fixture.CreateClientAs(GrantedPrincipal, ReaderRole);
        var grantedResponse = await grantedClient.GetAsync("/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=2.0.0");
        var grantedBody = await grantedResponse.Content.ReadAsStringAsync();
        grantedResponse.StatusCode.Should().Be(HttpStatusCode.OK, grantedBody);
        grantedBody.Should().Contain("test_layer");
    }

    private const string ReaderRole = "ogc-reader";

    private static async Task AssertRefusedAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        // An authenticated principal that fails authorization is Forbidden; only an
        // unauthenticated one is challenged (AccessPolicyHelpers.CreateAccessDeniedResult).
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        body.Should().NotContain("TransactionResponse");
        body.Should().NotContain("totalUpdated");
        body.Should().NotContain("totalDeleted");
    }

    private static async Task<HttpResponseMessage> PostTransactionAsync(HttpClient client, string requestBody)
    {
        using var content = new StringContent(requestBody, Encoding.UTF8, "application/xml");
        return await client.PostAsync("/wfs", content);
    }

    private static int CountFeatureMembers(string body)
    {
        if (!body.TrimStart().StartsWith('<'))
        {
            return 0;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(body);
        }
        catch (System.Xml.XmlException)
        {
            return 0;
        }

        XNamespace wfs = "http://www.opengis.net/wfs/2.0";
        return document.Descendants(wfs + "member").Count();
    }

    private static string InsertNameTransaction(string name) => $"""
        <wfs:Transaction service="WFS" version="2.0.0"
            xmlns:wfs="http://www.opengis.net/wfs/2.0"
            xmlns:gml="http://www.opengis.net/gml/3.2"
            xmlns:honua="http://honua.io/wfs">
          <wfs:Insert handle="authz-insert">
            <honua:test_layer>
              <honua:name>{name}</honua:name>
              <honua:shape>
                <gml:Point srsName="urn:ogc:def:crs:EPSG::4326">
                  <gml:pos>37.223 -122.556</gml:pos>
                </gml:Point>
              </honua:shape>
            </honua:test_layer>
          </wfs:Insert>
        </wfs:Transaction>
        """;

    private static string UpdateNameTransaction(long featureId, string name) => $"""
        <wfs:Transaction service="WFS" version="2.0.0"
            xmlns:wfs="http://www.opengis.net/wfs/2.0"
            xmlns:fes="http://www.opengis.net/fes/2.0"
            xmlns:honua="http://honua.io/wfs">
          <wfs:Update typeName="test_layer">
            <wfs:Property>
              <wfs:ValueReference>name</wfs:ValueReference>
              <wfs:Value>{name}</wfs:Value>
            </wfs:Property>
            <fes:Filter>
              <fes:ResourceId rid="test_layer.{featureId}" />
            </fes:Filter>
          </wfs:Update>
        </wfs:Transaction>
        """;

    private static string ReplaceNameTransaction(long featureId, string name) => $"""
        <wfs:Transaction service="WFS" version="2.0.0"
            xmlns:wfs="http://www.opengis.net/wfs/2.0"
            xmlns:fes="http://www.opengis.net/fes/2.0"
            xmlns:gml="http://www.opengis.net/gml/3.2"
            xmlns:honua="http://honua.io/wfs">
          <wfs:Replace>
            <honua:test_layer gml:id="test_layer.{featureId}">
              <honua:name>{name}</honua:name>
            </honua:test_layer>
            <fes:Filter>
              <fes:ResourceId rid="test_layer.{featureId}" />
            </fes:Filter>
          </wfs:Replace>
        </wfs:Transaction>
        """;

    private static string DeleteTransaction(long featureId) => $"""
        <wfs:Transaction service="WFS" version="2.0.0"
            xmlns:wfs="http://www.opengis.net/wfs/2.0"
            xmlns:fes="http://www.opengis.net/fes/2.0"
            xmlns:honua="http://honua.io/wfs">
          <wfs:Delete typeName="test_layer">
            <fes:Filter>
              <fes:ResourceId rid="test_layer.{featureId}" />
            </fes:Filter>
          </wfs:Delete>
        </wfs:Transaction>
        """;
}
