// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Honua.Server.Tests.Features.Licensing;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.LicenseManagement)]
public sealed class DisabledLicenseIntegrationTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/license")]
    [Endpoint("GET /api/v1/admin/license/status")]
    [Endpoint("GET /api/v1/admin/license/capacity")]
    [Endpoint("GET /api/v1/admin/license/features")]
    public async Task Production_DisabledWithoutLicense_StartsWithActiveEntitlementsAndTruthfulAdminResponses()
    {
        var fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment(Environments.Production);
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", "disabled-license-admin-key");
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Licensing:Mode"] = "Disabled",
                        ["Licensing:Edition"] = "Enterprise",
                        ["Licensing:LicensePath"] = null,
                        ["Licensing:LicenseContent"] = null,
                        ["Licensing:LicenseContentSecretRef"] = null,
                        ["Licensing:DevGrantEdition"] = null
                    }));
            });
        await fixture.InitializeAsync();
        try
        {
            Assert.Equal(Environments.Production, fixture.GetService<IHostEnvironment>().EnvironmentName);
            Assert.All(FeatureCatalog.All, feature =>
                Assert.True(LicenseGate.CheckEntitlement(fixture.Services, feature.Key).IsActive));
            Assert.DoesNotContain(fixture.Services.GetServices<IHostedService>(), service =>
                service is FileBackedLicenseService or LicenseCapacityMeter);
            Assert.False(fixture.GetService<ILicenseOperationPolicy>().IsBlocked);
            using var client = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", "disabled-license-admin-key"));
            foreach (var path in new[] { "/api/v1/admin/license", "/api/v1/admin/license/status" })
            {
                using var response = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var data = json.RootElement.GetProperty("data");
                Assert.Equal("disabled", data.GetProperty("mode").GetString());
                Assert.Equal("Unlicensed-2026.1", data.GetProperty("edition").GetString());
                Assert.Equal("Disabled", data.GetProperty("validationState").GetString());
                Assert.True(data.GetProperty("isValid").GetBoolean());
                Assert.False(data.GetProperty("expiryWarning").GetBoolean());
                Assert.True(!data.TryGetProperty("expiresAt", out var expires) || expires.ValueKind == JsonValueKind.Null);
                Assert.True(!data.TryGetProperty("daysUntilExpiry", out var days) || days.ValueKind == JsonValueKind.Null);
                var entitlements = data.GetProperty("entitlements").EnumerateArray().ToArray();
                Assert.Equal(FeatureCatalog.All.Count, entitlements.Length);
                Assert.All(entitlements, item => Assert.True(item.GetProperty("isActive").GetBoolean()));
                foreach (var key in new[] { "editing.featureserver-edits", "caching.redis", "geocoding.batch", "identity.oidc" })
                {
                    Assert.Contains(entitlements, item => item.GetProperty("key").GetString() == key && item.GetProperty("isActive").GetBoolean());
                }
            }

            using var capacityResponse = await client.GetAsync("/api/v1/admin/license/capacity");
            Assert.Equal(HttpStatusCode.OK, capacityResponse.StatusCode);
            using var capacityJson = JsonDocument.Parse(await capacityResponse.Content.ReadAsStringAsync());
            var capacity = capacityJson.RootElement.GetProperty("data");
            Assert.False(capacity.GetProperty("meteringEnabled").GetBoolean());
            Assert.False(capacity.GetProperty("registrationEnforced").GetBoolean());
            Assert.Equal(0, capacity.GetProperty("liveInstanceCount").GetInt32());

            using var featuresResponse = await client.GetAsync("/api/v1/admin/license/features");
            Assert.Equal(HttpStatusCode.OK, featuresResponse.StatusCode);
            using var featuresJson = JsonDocument.Parse(await featuresResponse.Content.ReadAsStringAsync());
            var features = featuresJson.RootElement.GetProperty("data");
            Assert.Equal("Unlicensed-2026.1", features.GetProperty("edition").GetString());
            Assert.All(features.GetProperty("features").EnumerateArray(), item =>
            {
                Assert.True(item.GetProperty("isEnabled").GetBoolean());
                Assert.False(item.GetProperty("upgradeRequired").GetBoolean());
            });

            using var unauthenticated = fixture.CreateClient();
            using var unauthorized = await unauthenticated.GetAsync("/api/v1/admin/license");
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
