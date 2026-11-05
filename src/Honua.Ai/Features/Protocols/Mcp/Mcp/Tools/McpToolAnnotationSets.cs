// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

internal static class McpToolAnnotationSets
{
    public static McpToolAnnotations ReadOnly(string title) => new()
    {
        Title = title,
        ReadOnlyHint = true,
        DestructiveHint = false,
    };

    public static McpToolAnnotations ReadOnlyOpenWorld(string title) => new()
    {
        Title = title,
        ReadOnlyHint = true,
        DestructiveHint = false,
        OpenWorldHint = true,
    };

    public static McpToolAnnotations Write(string title, bool destructive, bool idempotent) => new()
    {
        Title = title,
        ReadOnlyHint = false,
        DestructiveHint = destructive,
        IdempotentHint = idempotent,
    };
}
