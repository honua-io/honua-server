// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Studio.Drafts;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// Shared wire projection for the deterministic package-draft tools
/// (<c>honua_create_map_package</c> and <c>honua_create_app_package</c>).
/// Keeps the two adapters from duplicating finding rendering.
/// </summary>
internal static class McpPackageDraftProjection
{
    /// <summary>
    /// Projects shared draft findings onto their MCP wire shape.
    /// </summary>
    public static IReadOnlyList<McpPackageDraftFinding> MapFindings(IReadOnlyList<PackageDraftFinding> findings)
    {
        if (findings.Count == 0)
        {
            return [];
        }

        var mapped = new List<McpPackageDraftFinding>(findings.Count);
        foreach (var finding in findings)
        {
            mapped.Add(new McpPackageDraftFinding
            {
                Code = finding.Code,
                Path = finding.Path,
                Message = finding.Message
            });
        }

        return mapped;
    }

    /// <summary>
    /// Renders blocking findings into a single structured-error message.
    /// </summary>
    public static string DescribeErrors(string summary, IReadOnlyList<PackageDraftFinding> errors) =>
        errors.Count == 0
            ? summary + "."
            : summary + ": " + string.Join("; ", errors.Select(error => error.Describe())) + ".";
}
