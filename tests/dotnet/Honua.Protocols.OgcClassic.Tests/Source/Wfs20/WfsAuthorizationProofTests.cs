// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

/// <summary>
/// WFS 2.0 authorization proven through the protocol rather than at a unit seam
/// (#4387). Every assertion here is made against a real HTTP request handled by the
/// production WFS dispatcher, edit pipeline and PostGIS store; the only substituted
/// service is the <see cref="IRoleStore"/> that supplies the principal's grants, so a
/// non-admin identity can be injected without standing up an identity provider.
/// </summary>
/// <remarks>
/// Two layers are used because the two properties under test need opposite policy
/// shapes. <c>ServiceDataEditorAuthorization.EvaluateExplicitWritePolicy</c> makes an
/// explicit <see cref="AccessPolicy"/> write restriction authoritative — deliberately,
/// so a wildcard RBAC grant cannot widen it — which means a layer carrying
/// <c>AllowedRoles</c> or <c>AllowedWriteRoles</c> never reaches the per-operation
/// grant check at all. So:
/// <list type="bullet">
/// <item>layer 0 (<c>test_layer</c>) carries a read policy and proves read denial and
/// capabilities omission;</item>
/// <item>layer 1 (<c>related_test_layer_1</c>) carries no access policy, so its writes
/// are governed by per-operation RBAC grants and the BH3-001/BH3-014 narrowing in
/// <c>Wfs20Handler.Transaction.ValidateTransactionLayerWriteAccessAsync</c> is what is
/// actually exercised.</item>
/// </list>
/// </remarks>
[Collection("Database")]
[Protocol(TestProtocols.Wfs20)]
public sealed class WfsAuthorizationProofTests : IAsyncLifetime
{
    private static readonly XNamespace Wfs = "http://www.opengis.net/wfs/2.0";
    private static readonly XNamespace Gml = "http://www.opengis.net/gml/3.2";
    private static readonly XNamespace HonuaNs = "http://honua.io/wfs";

    /// <summary>Read-protected layer: <c>tests/seed/server.yaml</c> layer 0, objectids 1-5.</summary>
    private const string ReadProtectedType = "test_layer";

    /// <summary>
    /// Write target: <c>tests/seed/server.yaml</c> layer 1, objectids 101-104. The WFS
    /// local name is derived from the resource name "Related Test Layer 1" by
    /// <c>Wfs20Handler.BuildTypeLocalName</c>.
    /// </summary>
    private const string WriteType = "related_test_layer_1";
    private const string TargetFeature = WriteType + ".101";
    private const int WriteLayerId = 1;
    private const int SeededWriteFeatureCount = 4;
    private const string MutatedName = "changed-by-wfs";

    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro)
        .ConfigureWebHost(builder => builder
            .UseSetting("HONUA_DEV_AUTH", "false")
            .UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false"))
        .ConfigureServices(services =>
        {
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            services.PostConfigureAll<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                options.DefaultScheme = TestAuthHandler.SchemeName;
            });

            // Only the grant lookup is controlled. The HTTP dispatcher, the permission
            // resolver, the edit pipeline and PostGIS persistence stay production
            // services, so the enforcement point under test is the real one.
            var roles = Substitute.For<IRoleStore>();
            roles.GetEffectivePermissionsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(new EffectivePermissions
                {
                    UserId = call.ArgAt<string>(0),
                    Roles = call.ArgAt<IReadOnlyList<string>>(1),
                    Permissions = call.ArgAt<IReadOnlyList<string>>(1)
                        .Where(role => role is "query" or "insert" or "update" or "delete")
                        .Select(role => new PermissionGrant { Service = "*", Layer = "*", Operation = role })
                        .ToArray()
                }));
            services.RemoveAll<IRoleStore>();
            services.AddSingleton(roles);
            services.RemoveAll<IPermissionResolver>();
            services.AddSingleton<IPermissionResolver, PermissionResolver>();
        });

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        // Layer 0 only. Layer 1 is deliberately left without an access policy (see the
        // class remarks) so its writes route through the per-operation grant check.
        _fixture.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId, accessPolicy: new AccessPolicy
        {
            AllowAnonymous = false,
            AllowAnonymousWrite = false,
            AllowedRoles = ["query"]
        });
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("Update", "update", "totalUpdated")]
    [InlineData("Replace", "update", "totalReplaced")]
    [InlineData("Delete", "delete", "totalDeleted")]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "Transaction")]
    public async Task Transaction_InsertOnlyGrant_IsRefusedOverHttpAndMatchingGrantPersists(
        string action, string matchingGrant, string summaryElement)
    {
        using var reader = CreateClient("query");
        var before = await ReadFeaturesAsync(reader);
        before.Should().HaveCount(SeededWriteFeatureCount);
        NameOf(before, TargetFeature).Should().Be("Related Feature 1");

        // An insert-only grantee may not Update, Replace or Delete (BH3-001/BH3-014).
        using var insertOnly = CreateClient("query", "insert");
        using var denied = await insertOnly.PostAsync("/wfs", Transaction(action));
        var error = await denied.Content.ReadAsStringAsync();
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden, error);
        error.Should().NotContain("Related Feature 1").And.NotContain(MutatedName);
        var errorDocument = XDocument.Parse(error);
        errorDocument.Descendants(Wfs + "member").Should().BeEmpty();
        errorDocument.Descendants(Wfs + summaryElement).Should().BeEmpty();

        // The refusal changed nothing: same count, same ids, same attributes and same
        // ordinates, read back over the protocol as an authorized principal.
        var afterDenial = await ReadFeaturesAsync(reader);
        afterDenial.Select(static member => member.ToString()).Should()
            .Equal(before.Select(static member => member.ToString()),
                "a denied transaction must leave every member byte-identical");
        (await ReadStoredNameAsync(101)).Should().Be("Related Feature 1",
            "the denial must hold in PostGIS, not merely in the response");

        // The same request with the matching grant succeeds and is durable.
        using var permitted = CreateClient("query", matchingGrant);
        using var accepted = await permitted.PostAsync("/wfs", Transaction(action));
        var body = await accepted.Content.ReadAsStringAsync();
        accepted.StatusCode.Should().Be(HttpStatusCode.OK, body);
        XDocument.Parse(body).Descendants(Wfs + summaryElement).Single().Value.Should().Be("1");

        var after = await ReadFeaturesAsync(reader);
        after.Should().HaveCount(action == "Delete" ? SeededWriteFeatureCount - 1 : SeededWriteFeatureCount);
        if (action == "Delete")
        {
            IdsOf(after).Should().NotContain(TargetFeature);
            (await ReadStoredNameAsync(101)).Should().BeNull();
        }
        else
        {
            NameOf(after, TargetFeature).Should().Be(MutatedName);
            (await ReadStoredNameAsync(101)).Should().Be(MutatedName);
        }

        // Only the targeted feature moved.
        Untargeted(after).Should().Equal(Untargeted(before));
    }

    [IntegrationTest]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "Transaction")]
    public async Task Transaction_UpdateOnlyGrant_CannotInsert()
    {
        // The narrowing runs in both directions: an update grant is not an insert
        // grant. Without the per-operation check either grant would authorize both.
        using var reader = CreateClient("query");
        (await ReadFeaturesAsync(reader)).Should().HaveCount(SeededWriteFeatureCount);

        using var updateOnly = CreateClient("query", "update");
        using var denied = await updateOnly.PostAsync("/wfs", Transaction("Insert"));
        var error = await denied.Content.ReadAsStringAsync();
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden, error);
        (await ReadFeaturesAsync(reader)).Should().HaveCount(SeededWriteFeatureCount);

        using var inserter = CreateClient("query", "insert");
        using var accepted = await inserter.PostAsync("/wfs", Transaction("Insert"));
        var body = await accepted.Content.ReadAsStringAsync();
        accepted.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var after = await ReadFeaturesAsync(reader);
        after.Should().HaveCount(SeededWriteFeatureCount + 1);
        after.SelectMany(static member => member.Elements())
            .Select(static feature => feature.Element(HonuaNs + "name")?.Value)
            .Should().Contain(MutatedName);
    }

    [IntegrationTest]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetCapabilities")]
    public async Task Read_WithoutGrant_DisclosesNoMembersAndOmitsProtectedType()
    {
        using var reader = CreateClient("query");
        (await ReadFeaturesAsync(reader, ReadProtectedType)).Should().HaveCount(5);

        using var denied = CreateClient("unrelated-role");
        using var response = await denied.GetAsync(FeatureUrl(ReadProtectedType));
        var body = await response.Content.ReadAsStringAsync();

        // A withheld type is deliberately indistinguishable from an unknown one, so the
        // status is the unknown-type status. What matters for the security floor is
        // that no member and no attribute value crosses the wire.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("InvalidParameterValue");
        body.Should().NotContain("Test Feature").And.NotContain("Another Feature")
            .And.NotContain("Fifth Feature").And.NotContain("-122.5");
        var errorDocument = XDocument.Parse(body);
        errorDocument.Descendants(Wfs + "member").Should().BeEmpty();
        errorDocument.Descendants(Gml + "Point").Should().BeEmpty();

        // ... and the type is absent from the denied principal's capabilities while
        // remaining present for the granted principal, so the omission is the access
        // policy at work rather than a broken document.
        const string capabilitiesUrl = "/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=2.0.0";
        var deniedCapabilities = XDocument.Parse(await denied.GetStringAsync(capabilitiesUrl));
        FeatureTypeNames(deniedCapabilities).Should().NotContain($"honua:{ReadProtectedType}");
        FeatureTypeNames(deniedCapabilities).Should().Contain($"honua:{WriteType}",
            "only the read-protected type is withheld");

        var allowedCapabilities = XDocument.Parse(await reader.GetStringAsync(capabilitiesUrl));
        FeatureTypeNames(allowedCapabilities).Should().Contain($"honua:{ReadProtectedType}");
    }

    private static string FeatureUrl(string typeName) =>
        $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES={typeName}&SORTBY=objectid";

    private static IEnumerable<string> FeatureTypeNames(XDocument capabilities) => capabilities
        .Descendants(Wfs + "FeatureType")
        .Select(static type => type.Element(Wfs + "Name")!.Value);

    private HttpClient CreateClient(params string[] roles) => _fixture.CreateClient(client =>
    {
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "wfs-proof-user");
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(',', roles));
    });

    private static async Task<XElement[]> ReadFeaturesAsync(HttpClient client, string typeName = WriteType)
    {
        using var response = await client.GetAsync(FeatureUrl(typeName));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return XDocument.Parse(body).Descendants(Wfs + "member").ToArray();
    }

    private Task<string?> ReadStoredNameAsync(long objectId)
        => _fixture.ReadStoredFeatureNameAsync(WriteLayerId, objectId);

    private static IEnumerable<string> IdsOf(IEnumerable<XElement> members) => members
        .SelectMany(static member => member.Elements())
        .Select(static feature => feature.Attribute(Gml + "id")?.Value ?? string.Empty);

    private static string? NameOf(IEnumerable<XElement> members, string gmlId) => members
        .SelectMany(static member => member.Elements())
        .SingleOrDefault(feature => feature.Attribute(Gml + "id")?.Value == gmlId)
        ?.Element(HonuaNs + "name")?.Value;

    private static string[] Untargeted(IEnumerable<XElement> members) => members
        .Where(static member => !member.ToString().Contains(TargetFeature, StringComparison.Ordinal))
        .Select(static member => member.ToString())
        .ToArray();

    private static StringContent Transaction(string action)
    {
        const string filter = "<fes:Filter><fes:ResourceId rid=\"" + TargetFeature + "\" /></fes:Filter>";
        var operation = action switch
        {
            "Insert" =>
                $"<wfs:Insert><honua:{WriteType}><honua:name>{MutatedName}</honua:name></honua:{WriteType}></wfs:Insert>",
            "Update" =>
                $"<wfs:Update typeName=\"{WriteType}\"><wfs:Property><wfs:ValueReference>name</wfs:ValueReference>" +
                $"<wfs:Value>{MutatedName}</wfs:Value></wfs:Property>{filter}</wfs:Update>",
            "Replace" =>
                $"<wfs:Replace><honua:{WriteType} gml:id=\"{TargetFeature}\"><honua:name>{MutatedName}</honua:name>" +
                $"</honua:{WriteType}>{filter}</wfs:Replace>",
            "Delete" => $"<wfs:Delete typeName=\"{WriteType}\">{filter}</wfs:Delete>",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

        return new StringContent(
            "<wfs:Transaction service=\"WFS\" version=\"2.0.0\" " +
            "xmlns:wfs=\"http://www.opengis.net/wfs/2.0\" xmlns:fes=\"http://www.opengis.net/fes/2.0\" " +
            "xmlns:gml=\"http://www.opengis.net/gml/3.2\" xmlns:honua=\"http://honua.io/wfs\">" +
            operation +
            "</wfs:Transaction>",
            Encoding.UTF8,
            "application/xml");
    }
}
