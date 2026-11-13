// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Infrastructure.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.TestKit;

internal static class EmulatedHumanApprovalSession
{
    internal static async Task<HttpClient> CreateClientAsync(IServiceProvider services, Func<HttpClient> createClient)
    {
        using var scope = services.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<AdminAuthSessionStore>();
        var sessionId = await sessions.CreateAuthenticatedSessionAsync(new AdminAuthAuthenticatedSession
        {
            ProviderKey = "emulated-identity",
            AccessToken = "emulated-interactive-token",
            Origin = AdminAuthSessionOrigin.InteractiveOidc,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            Claims =
            [
                new() { Type = "iss", Value = "https://identity.example.test" },
                new() { Type = ClaimTypes.NameIdentifier, Value = "emulated-reviewer" },
                new() { Type = ClaimTypes.Role, Value = "admin" },
            ],
        }, CancellationToken.None);
        var client = createClient();
        client.BaseAddress = new Uri("https://localhost");
        client.DefaultRequestHeaders.Add("Cookie", $"{AdminAuthSessionStore.AuthSessionCookieName}={sessionId}");
        using var response = await client.GetAsync("/api/v1/admin/auth/csrf");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add("X-Honua-CSRF", document.RootElement.GetProperty("requestToken").GetString());
        return client;
    }
}
