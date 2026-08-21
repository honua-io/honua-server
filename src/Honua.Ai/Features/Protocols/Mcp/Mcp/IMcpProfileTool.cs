// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp;

/// <summary>Marks an MCP tool as belonging to an opt-in conformance profile.</summary>
internal interface IMcpProfileTool
{
    string ProfileName { get; }
}
