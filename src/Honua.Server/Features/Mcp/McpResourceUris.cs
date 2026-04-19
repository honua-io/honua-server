// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Mcp;

/// <summary>
/// Canonical URI strings and prefixes for MCP resources advertised by the
/// operator surface. Centralized so the resource registry, taxonomy-alignment
/// tests, and tool outputs reference the same values.
/// </summary>
internal static class McpResourceUris
{
    public const string Scheme = "honua";

    public const string JobsPrefix = "honua://jobs/";
    public const string JobResultsSuffix = "/results";
    public const string WorkspacesPrefix = "honua://workspaces/";
    public const string CatalogProcesses = "honua://catalog/processes";

    /// <summary>Builds the <c>honua://jobs/{jobId}</c> URI.</summary>
    public static string JobUri(string jobId) => $"{JobsPrefix}{jobId}";

    /// <summary>Builds the <c>honua://jobs/{jobId}/results</c> URI.</summary>
    public static string JobResultsUri(string jobId) => $"{JobsPrefix}{jobId}{JobResultsSuffix}";

    /// <summary>Builds the <c>honua://workspaces/{workspaceId}</c> URI.</summary>
    public static string WorkspaceUri(string workspaceId) => $"{WorkspacesPrefix}{workspaceId}";
}
