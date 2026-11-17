// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp;

// MCP logging utility levels (RFC 5424 severities, least to most severe).
internal static class McpLogging
{
    public const string Info = "info";

    public const string Error = "error";

    public const string JobsLogger = "honua.jobs";

    private static readonly string[] Levels =
        ["debug", Info, "notice", "warning", Error, "critical", "alert", "emergency"];

    public static bool IsKnownLevel(string? level) =>
        level is not null && Array.IndexOf(Levels, level) >= 0;

    // A session without a setLevel choice receives info and above.
    public static bool IsEnabled(string? sessionLevel, string messageLevel) =>
        Array.IndexOf(Levels, messageLevel) >= Math.Max(0, Array.IndexOf(Levels, sessionLevel ?? Info));
}
