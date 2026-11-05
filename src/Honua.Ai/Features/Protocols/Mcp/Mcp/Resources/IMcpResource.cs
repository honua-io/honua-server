// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Resources;

internal interface IMcpResource
{
    string Family { get; }

    string ResolveFamily(string uri) => Family;

    IReadOnlyList<McpResourceDescriptor> Describe();

    IReadOnlyList<McpResourceTemplateDescriptor> DescribeTemplates();

    bool CanHandle(string uri);

    Task<McpResourcesReadResult> ReadAsync(
        HttpContext httpContext,
        string uri,
        CancellationToken cancellationToken);
}
