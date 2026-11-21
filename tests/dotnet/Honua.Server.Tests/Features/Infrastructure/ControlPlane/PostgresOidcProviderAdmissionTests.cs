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
public sealed class PostgresOidcProviderAdmissionTests(PostgresOidcProviderAdmissionTests.AdmissionHosts hosts)
    : IClassFixture<PostgresOidcProviderAdmissionTests.AdmissionHosts>
{
    [IntegrationTheory]
    [InlineData("Pro", "Second")]
    [InlineData("Pro", "")]
    [InlineData("Community", "Second")]
    [InlineData("Community", "")]
    [Endpoint("POST /api/v1/admin/oidc/providers")]
    public async Task CreateProvider_UnreadableExistingProvider_ReturnsMultiProviderEntitlementBeforeValidation(string edition, string name)
    {
        var fixture = await hosts.GetApplicationAsync(edition);
        using var client = fixture.CreateAdminClient();
        using var response = await client.PostAsJsonAsync("/api/v1/admin/oidc/providers", new
        {
            name,
            providerType = "Generic",
            authority = "https://idp.example.com",
            clientId = "second"
        });

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        Assert.Contains("identity.oidc-multi-provider", await response.Content.ReadAsStringAsync());
    }

    // One harness holding one provider the hosts cannot read, and one host per edition. Every case
    // is refused before anything is written, so the cases of an edition share its host.
    public sealed class AdmissionHosts : IAsyncLifetime
    {
        private readonly DatabaseFixtureAdapter _database = new();
        private readonly Dictionary<string, WebAppFixture> _applications = new(StringComparer.Ordinal);
        private ControlPlaneStateHarness? _harness;

        public async Task InitializeAsync()
        {
            await _database.InitializeAsync();
            _harness = await ControlPlaneStateHarness.CreateAsync(_database, nameof(PostgresOidcProviderAdmissionTests));
            var foreign = new PostgresOidcProviderStore(_harness.CreateConfigurationDocuments(new EphemeralDataProtectionProvider()));
            await foreign.CreateProviderAsync(new OidcProviderConfiguration
            {
                Name = "Existing",
                ProviderType = "Generic",
                Authority = "https://idp.example.com",
                ClientId = "existing"
            });
        }

        public async Task<WebAppFixture> GetApplicationAsync(string edition)
        {
            if (_applications.TryGetValue(edition, out var started))
            {
                return started;
            }

            var harness = _harness ?? throw new InvalidOperationException("The hosts must be initialized first.");
            var application = new WebAppFixture().UseSeed("tests/seed/server.yaml").ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("Licensing:DevGrantEdition", edition);
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IOidcProviderStore>();
                    services.AddSingleton<IOidcProviderStore>(harness.CreateOidcStore());
                });
            });
            try
            {
                await application.InitializeAsync();
            }
            catch
            {
                await application.DisposeAsync();
                throw;
            }

            _applications[edition] = application;
            return application;
        }

        public async Task DisposeAsync()
        {
            try
            {
                foreach (var application in _applications.Values)
                {
                    await application.DisposeAsync();
                }
            }
            finally
            {
                await DisposeStorageAsync();
            }
        }

        private async Task DisposeStorageAsync()
        {
            try
            {
                await (_harness?.DisposeAsync() ?? ValueTask.CompletedTask);
            }
            finally
            {
                await _database.DisposeAsync();
            }
        }
    }
}
