// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Identity.Domain;
using Honua.Server.Features.Infrastructure.Configuration;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.IdentityManagement)]
public sealed class PostgresOidcProviderAdmissionTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTheory]
    [InlineData("Pro", "Second")]
    [InlineData("Pro", "")]
    [InlineData("Community", "Second")]
    [InlineData("Community", "")]
    [Endpoint("POST /api/v1/admin/oidc/providers")]
    public async Task CreateProvider_UnreadableExistingProvider_ReturnsMultiProviderEntitlementBeforeValidation(string edition, string name)
    {
        await using var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresOidcProviderAdmissionTests));
        var foreign = new PostgresOidcProviderStore(harness.CreateConfigurationDocuments(new EphemeralDataProtectionProvider()));
        await foreign.CreateProviderAsync(new OidcProviderConfiguration
        {
            Name = "Existing", ProviderType = "Generic", Authority = "https://idp.example.com", ClientId = "existing"
        });
        var fixture = new WebAppFixture().UseSeed("tests/seed/server.yaml").ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("Licensing:DevGrantEdition", edition);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IOidcProviderStore>();
                services.AddSingleton<IOidcProviderStore>(harness.CreateOidcStore());
            });
        });
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            using var response = await client.PostAsJsonAsync("/api/v1/admin/oidc/providers", new
            {
                name, providerType = "Generic", authority = "https://idp.example.com", clientId = "second"
            });

            Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
            Assert.Contains("identity.oidc-multi-provider", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
