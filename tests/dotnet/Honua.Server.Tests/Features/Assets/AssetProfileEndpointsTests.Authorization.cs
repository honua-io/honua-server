// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Core.Features.Assets;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Tests.Features.Admin;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Assets;

public sealed partial class AssetProfileEndpointsTests
{
    private static WebAppFixture CreateAuthenticatedFixture() => new WebAppFixture()
        .ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        })
        .ConfigureServices(services =>
        {
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, RlsClaimsTestAuthHandler>(
                RlsClaimsTestAuthHandler.SchemeName, _ => { });
            services.PostConfigureAll<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = RlsClaimsTestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = RlsClaimsTestAuthHandler.SchemeName;
                options.DefaultScheme = RlsClaimsTestAuthHandler.SchemeName;
            });
        });

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/assets/profiles")]
    public async Task CreateProfile_Anonymous_CannotWrite()
    {
        using var client = _fixture.CreateClient();
        await AssertCreateDeniedAsync(client, HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/assets/profiles")]
    public async Task CreateProfile_Reader_CannotWrite()
    {
        using var client = _fixture.CreateClient();
        SetPrincipal(client, "asset-reader", "reader");
        await AssertCreateDeniedAsync(client, HttpStatusCode.Forbidden);
    }

    private async Task AssertCreateDeniedAsync(HttpClient client, HttpStatusCode expectedStatus)
    {
        using var response = await client.PostAsJsonAsync(ProfilesPath, ProfileRequest(Guid.NewGuid()),
            AssetsJsonContext.Default.AssetProfileWriteRequest);

        response.StatusCode.Should().Be(expectedStatus);
        (await AssetTestData.CountAsync(_fixture, "asset_profiles")).Should().Be(0);
        (await AssetTestData.CountAsync(_fixture, "asset_command_receipts")).Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/assets/profiles")]
    [Endpoint("GET /api/v1/assets/profiles")]
    [Endpoint("GET /api/v1/assets/profiles/{profileId}")]
    [Endpoint("PUT /api/v1/assets/profiles/{profileId}")]
    public async Task ProfileOperations_ServiceEditorWithoutAdminRole_CanManageProfile()
    {
        using var editor = CreateEditorClient();
        using var created = await editor.PostAsJsonAsync(ProfilesPath, ProfileRequest(Guid.NewGuid()),
            AssetsJsonContext.Default.AssetProfileWriteRequest);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var profileId = (await AssetTestData.ReadJsonAsync(created))["profile"]!["profileId"]!.GetValue<string>();

        using var get = await editor.GetAsync($"{ProfilesPath}/{profileId}");
        var list = await editor.GetFromJsonAsync($"{ProfilesPath}?serviceId={WebAppFixture.TestServiceId}",
            AssetsJsonContext.Default.AssetProfileListResult);
        using var updated = await editor.PutAsJsonAsync($"{ProfilesPath}/{profileId}",
            new AssetProfileWriteRequest { CommandId = Guid.NewGuid(), ExpectedVersion = 1, State = AssetProfileState.Active },
            AssetsJsonContext.Default.AssetProfileWriteRequest);

        get.StatusCode.Should().Be(HttpStatusCode.OK);
        list!.Items.Should().ContainSingle();
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTheory]
    [InlineData(IdentityRestriction.RowFilter)]
    [InlineData(IdentityRestriction.MaskedId)]
    [Operation(Operations.Update)]
    [Endpoint("PUT /api/v1/assets/profiles/{profileId}")]
    public async Task ActivateProfile_IncompleteIdentityVisibility_RejectsWithoutAdvancingVersion(IdentityRestriction restriction)
    {
        var profileId = await CreateProfileAsync();
        using var admin = _fixture.CreateAdminClient();
        var policyPath = restriction == IdentityRestriction.MaskedId ? "/api/v1/admin/field-mask-policies" : "/api/v1/admin/rls-policies";
        var policyId = await CreateIdentityRestrictionAsync(admin, restriction);
        try
        {
            using var editor = CreateEditorClient();
            using var response = await editor.PutAsJsonAsync($"{ProfilesPath}/{profileId}",
                new AssetProfileWriteRequest { CommandId = Guid.NewGuid(), ExpectedVersion = 1, State = AssetProfileState.Active },
                AssetsJsonContext.Default.AssetProfileWriteRequest);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var errors = (await AssetTestData.ReadJsonAsync(response))["errors"]!.AsArray();
            errors.Should().Contain(error => error!["field"]!.GetValue<string>() == (restriction == IdentityRestriction.MaskedId ? "fields.assetId" : "state"));
            var stored = await _fixture.Client.GetFromJsonAsync($"{ProfilesPath}/{profileId}", AssetsJsonContext.Default.AssetProfile);
            stored!.Version.Should().Be(1);
            stored.State.Should().Be(AssetProfileState.Draft);
            (await AssetTestData.CountAsync(_fixture, "asset_command_receipts")).Should().Be(1);
        }
        finally
        {
            using var removed = await admin.DeleteAsync($"{policyPath}/{policyId}");
            removed.IsSuccessStatusCode.Should().BeTrue();
        }
    }

    private static async Task<Guid> CreateIdentityRestrictionAsync(HttpClient admin, IdentityRestriction restriction)
    {
        var role = $"data-editor:{WebAppFixture.TestServiceId}";
        using var content = restriction == IdentityRestriction.MaskedId
            ? JsonContent.Create(new CreateFieldMaskPolicyRequest
            {
                Role = role, Service = "*", Layer = "*", Attribute = "sign_id",
            }, FieldMaskPolicyJsonContext.Default.CreateFieldMaskPolicyRequest)
            : JsonContent.Create(new CreateRlsPolicyRequest
            {
                Role = role, Service = "*", Layer = "*", Attribute = "sign_id", ClaimType = "category", Comparison = "in",
            }, RlsPolicyJsonContext.Default.CreateRlsPolicyRequest);
        using var response = await admin.PostAsync(
            restriction == IdentityRestriction.MaskedId ? "/api/v1/admin/field-mask-policies" : "/api/v1/admin/rls-policies", content);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await AssetTestData.ReadJsonAsync(response);
        return Guid.Parse(body["data"]!["policyId"]!.GetValue<string>());
    }

    public enum IdentityRestriction
    {
        RowFilter,
        MaskedId,
    }

    private HttpClient CreateEditorClient() => _fixture.CreateClient(client =>
        SetPrincipal(client, "asset-editor", $"data-editor:{WebAppFixture.TestServiceId}"));

    private static void SetPrincipal(HttpClient client, string user, string role)
    {
        client.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.UserHeader, user);
        client.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.RolesHeader, role);
    }
}
