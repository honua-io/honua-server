// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Sdk.Admin;

/// <summary>
/// Configuration options for the Honua Admin client.
/// </summary>
public sealed class HonuaAdminClientOptions
{
    /// <summary>
    /// Base address of the Honua server.
    /// </summary>
    public Uri BaseAddress { get; set; } = new("http://localhost:5000");

    /// <summary>
    /// API key for admin authentication.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Bearer token for authentication.
    /// </summary>
    public string? BearerToken { get; set; }
}
