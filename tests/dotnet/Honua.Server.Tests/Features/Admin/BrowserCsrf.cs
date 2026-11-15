// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Server.Tests.Features.Admin;

// Browser-session writes carry the CSRF request token the session issues; fetch it and send it on every request.
internal static class BrowserCsrf
{
    internal const string HeaderName = "X-Honua-CSRF";

    public static async Task SetHeaderAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/admin/auth/csrf");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Remove(HeaderName);
        client.DefaultRequestHeaders.Add(HeaderName, document.RootElement.GetProperty("requestToken").GetString());
    }
}
