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

[Collection("Database")]
[Protocol(TestProtocols.Wfs20)]
public sealed class WfsAuthorizationProofTests : IAsyncLifetime
{
    private static readonly XNamespace Wfs = "http://www.opengis.net/wfs/2.0";
    private static readonly XNamespace Honua = "http://honua.io/wfs";
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
            // Only identity/grant inputs are controlled: the HTTP dispatcher, permission
            // resolver, edit pipeline and PostGIS persistence remain production services.
            var roles = Substitute.For<IRoleStore>();
            roles.GetEffectivePermissionsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(new EffectivePermissions
                {
                    UserId = call.ArgAt<string>(0),
                    Roles = call.ArgAt<IReadOnlyList<string>>(1),
                    Permissions = call.ArgAt<IReadOnlyList<string>>(1)
                        .Where(role => role is "query" or "insert" or "update" or "delete")
                        .Select(role => new PermissionGrant { Service = "*", Layer = "*", Operation = role }).ToArray()
                }));
            services.RemoveAll<IRoleStore>();
            services.AddSingleton(roles);
            services.RemoveAll<IPermissionResolver>();
            services.AddSingleton<IPermissionResolver, PermissionResolver>();
        });

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId, accessPolicy: new AccessPolicy
        {
            AllowAnonymous = false,
            AllowAnonymousWrite = false,
            AllowedRoles = ["query"],
            AllowedWriteRoles = ["coarse-write-not-granted"]
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
    public async Task Transaction_InsertOnlyGrant_DeniesMutationAndMatchingGrantPersists(
        string action, string grant, string summary)
    {
        using var reader = CreateClient("query");
        var before = await ReadMembersAsync(reader);
        before.Should().HaveCount(5);
        before.Single(member => member.Element(Honua + "test_layer")!.Attribute("{http://www.opengis.net/gml/3.2}id")!.Value == "test_layer.1")
            .Descendants(Honua + "name").Single().Value.Should().Be("Test Feature");
        using var insertOnly = CreateClient("query", "insert");
        using var denied = await insertOnly.PostAsync("/wfs", Transaction(action));
        var error = await denied.Content.ReadAsStringAsync();
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden, error);
        error.Should().Contain("Access to this resource is forbidden.").And.NotContain("Test Feature").And.NotContain("changed-by-wfs");
        XDocument.Parse(error).Descendants("{http://www.opengis.net/ows/1.1}Exception").Single()
            .Attribute("exceptionCode")!.Value.Should().Be("NoApplicableCode");
        XDocument.Parse(error).Descendants(Wfs + "member").Should().BeEmpty();
        (await ReadMembersAsync(reader)).Select(member => member.ToString()).Should()
            .Equal(before.Select(member => member.ToString()), "denial must preserve count, attributes and geometry");

        using var permitted = CreateClient("query", grant);
        using var accepted = await permitted.PostAsync("/wfs", Transaction(action));
        var body = await accepted.Content.ReadAsStringAsync();
        accepted.StatusCode.Should().Be(HttpStatusCode.OK, body);
        XDocument.Parse(body).Descendants(Wfs + summary).Single().Value.Should().Be("1");
        var after = await ReadMembersAsync(reader);
        after.Should().HaveCount(action == "Delete" ? 4 : 5);
        var target = after.SelectMany(member => member.Elements())
            .SingleOrDefault(feature => feature.Attribute("{http://www.opengis.net/gml/3.2}id")!.Value == "test_layer.1");
        if (action == "Delete")
        {
            target.Should().BeNull();
        }
        else
        {
            target.Should().NotBeNull();
            target!.Element(Honua + "name")!.Value.Should().Be("changed-by-wfs");
        }
        after.Where(member => !member.ToString().Contains("test_layer.1", StringComparison.Ordinal))
            .Select(member => member.ToString()).Should().Equal(before
                .Where(member => !member.ToString().Contains("test_layer.1", StringComparison.Ordinal))
                .Select(member => member.ToString()));
    }

    [IntegrationTest]
    [Operation(Operations.SecurityTesting)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetCapabilities")]
    public async Task Read_WithoutGrant_DisclosesNoMembersAndOmitsProtectedType()
    {
        using var reader = CreateClient("query");
        (await ReadMembersAsync(reader)).Should().HaveCount(5);
        using var denied = CreateClient("unrelated-role");
        using var response = await denied.GetAsync(FeatureUrl);
        var body = await response.Content.ReadAsStringAsync();
        // A hidden type is deliberately indistinguishable from an unknown type.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("InvalidParameterValue").And.NotContain("Test Feature");
        XDocument.Parse(body).Descendants(Wfs + "member").Should().BeEmpty();
        const string capabilitiesUrl = "/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=2.0.0";
        var capabilities = XDocument.Parse(await denied.GetStringAsync(capabilitiesUrl));
        capabilities.Descendants(Wfs + "FeatureType").Select(type => type.Element(Wfs + "Name")!.Value)
            .Should().NotContain("honua:test_layer");
        var allowedCapabilities = XDocument.Parse(await reader.GetStringAsync(capabilitiesUrl));
        allowedCapabilities.Descendants(Wfs + "FeatureType").Select(type => type.Element(Wfs + "Name")!.Value)
            .Should().Contain("honua:test_layer");
    }

    private const string FeatureUrl = "/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES=test_layer&SORTBY=objectid";

    private HttpClient CreateClient(params string[] roles) => _fixture.CreateClient(client =>
    {
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "wfs-proof-user");
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(',', roles));
    });

    private static async Task<XElement[]> ReadMembersAsync(HttpClient client)
    {
        using var response = await client.GetAsync(FeatureUrl);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return XDocument.Parse(body).Descendants(Wfs + "member").ToArray();
    }

    private static StringContent Transaction(string action)
    {
        const string filter = "<fes:Filter><fes:ResourceId rid=\"test_layer.1\" /></fes:Filter>";
        var operation = action switch
        {
            "Update" => $"<wfs:Update typeName=\"test_layer\"><wfs:Property><wfs:ValueReference>name</wfs:ValueReference><wfs:Value>changed-by-wfs</wfs:Value></wfs:Property>{filter}</wfs:Update>",
            "Replace" => $"<wfs:Replace><honua:test_layer gml:id=\"test_layer.1\"><honua:name>changed-by-wfs</honua:name></honua:test_layer>{filter}</wfs:Replace>",
            "Delete" => $"<wfs:Delete typeName=\"test_layer\">{filter}</wfs:Delete>",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        return new StringContent($"<wfs:Transaction service=\"WFS\" version=\"2.0.0\" xmlns:wfs=\"http://www.opengis.net/wfs/2.0\" xmlns:fes=\"http://www.opengis.net/fes/2.0\" xmlns:gml=\"http://www.opengis.net/gml/3.2\" xmlns:honua=\"http://honua.io/wfs\">{operation}</wfs:Transaction>", Encoding.UTF8, "application/xml");
    }
}
