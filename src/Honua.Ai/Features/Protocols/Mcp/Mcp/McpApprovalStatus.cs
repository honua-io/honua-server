// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp;

// One name for "a human approval is pending" across MCP. Proposals, deploy operations and
// publication status report AwaitingApproval. Operation handles keep RequiresApproval on the
// wire, and inputs accept it as an alias, so an agent can pass back whichever name it saw.
internal static class McpApprovalStatus
{
    public const string Canonical = "AwaitingApproval";

    public const string HandleAlias = "RequiresApproval";

    public static string? Normalize(string? status) =>
        string.Equals(status?.Trim(), HandleAlias, StringComparison.OrdinalIgnoreCase) ? Canonical : status;
}
