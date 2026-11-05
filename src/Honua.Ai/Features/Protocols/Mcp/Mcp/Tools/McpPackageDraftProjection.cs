// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Studio.Drafts;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

internal static class McpPackageDraftProjection
{
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

    public static string DescribeErrors(string summary, IReadOnlyList<PackageDraftFinding> errors) =>
        errors.Count == 0
            ? summary + "."
            : summary + ": " + string.Join("; ", errors.Select(error => error.Describe())) + ".";
}
