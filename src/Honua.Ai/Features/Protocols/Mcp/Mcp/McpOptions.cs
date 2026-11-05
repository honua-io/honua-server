// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp;

internal enum McpSessionEvictionPolicy
{
    EvictLeastRecentlyUsed,

    RejectNew
}

internal sealed class McpOptions
{
    public const string SectionName = "Mcp";

    public bool ServerInitiatedStreamEnabled { get; set; }

    // An unknown session id is not served as session-less outside Development, because that drops the view negotiated at initialize.
    public bool StatelessSessionFallback { get; set; }

    internal static bool DefaultStatelessSessionFallback(IHostEnvironment? environment) =>
        environment?.IsDevelopment() == true;

    public McpSessionModeSetting SessionMode { get; set; } = McpSessionModeSetting.Auto;

    // When no signing key is configured, the shared database connection string is the HKDF input.
    public string? SessionSigningKey { get; set; }

    internal static readonly TimeSpan DefaultStatelessSessionLifetime = TimeSpan.FromHours(8);

    public TimeSpan StatelessSessionLifetime { get; set; } = DefaultStatelessSessionLifetime;

    public int MaxSessions { get; set; } = 10_000;

    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    public McpSessionEvictionPolicy SessionEvictionPolicy { get; set; }
        = McpSessionEvictionPolicy.EvictLeastRecentlyUsed;

    public int SessionCapacityRetryAfterSeconds { get; set; } = 5;

    public int MaxAnonymousSessions { get; set; } = 1_000;

    public const int DefaultMaxSessionsPerPrincipal = 32;

    public int MaxSessionsPerPrincipal { get; set; } = DefaultMaxSessionsPerPrincipal;

    public int MaxBatchSize { get; set; } = 50;

    public int MaxAnonymousBatchSize { get; set; } = 5;

    public long MaxRequestBodyBytes { get; set; } = 10L * 1024 * 1024;
}
