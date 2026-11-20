// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class AdminAuthEndpointsTests
{
    // A bearer minted from the administrator password has no session to end, so rotating the
    // password is what revokes it, as for portal tokens minted from the password.
    [Protocol(TestProtocols.Admin)]
    [Operation(Operations.Configuration)]
    public sealed class PasswordTokenRotation
    {
        [IntegrationTest]
        [Endpoint("POST /api/v1/admin/auth/password-token")]
        [Endpoint("GET /api/v1/admin/config")]
        public async Task PasswordBearer_StopsWorkingWhenThePasswordIsRotated()
        {
            await using var fixture = CreatePasswordTokenFixture();
            await fixture.InitializeAsync();
            using var api = fixture.CreateClient();
            api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await MintAsync(fixture.CreateClient()));
            using var before = await api.GetAsync("/api/v1/admin/config");
            before.StatusCode.Should().Be(HttpStatusCode.OK);

            fixture.Services.GetRequiredService<IOptions<ApiKeyAuthenticationOptions>>().Value.AdminPassword =
                "rotated-admin-key-7f3c9a1e5b";

            using var after = await api.GetAsync("/api/v1/admin/config");
            after.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the password the bearer was minted from is gone");
        }

        private static async Task<string> MintAsync(HttpClient browser)
        {
            using (browser)
            {
                browser.BaseAddress = new Uri("https://localhost");
                browser.DefaultRequestHeaders.Add("Origin", "https://console.example.com");
                using var response = await browser.PostAsJsonAsync("/api/v1/admin/auth/password-token", new { password = "test-admin-key" });
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                var token = await response.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminOperatorBearerResponse);
                return token!.AccessToken;
            }
        }
    }
}
