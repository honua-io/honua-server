// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp;

public sealed class McpPublishedOperationOptions
{
    public const string SectionName = "Mcp:PublishOperations";

    public bool Enabled { get; set; }

    public bool AdminProjection { get; set; } = true;

    public bool DeterministicOnly { get; set; }
}
