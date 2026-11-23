// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Server.Features.WorkflowPackages;

// Bounds on what a client may send when it names or saves a workflow package.
internal static class WorkflowPackageRequestLimits
{
    internal const int MaxIdentifierLength = 128;
    internal const int MaxNameLength = 200;
    internal const int MaxDescriptionLength = 4_000;
    internal const int MaxNodes = 500;
    internal const int MaxEdges = 2_000;
    internal const int MaxMetadataEntries = 64;

    // Client-chosen package and publication ids become route segments, a Location header and
    // part of the schedule definition id "workflow-package:{packageId}:v{n}:{publicationId}".
    // Letters, digits, '.', '_' and '-' only, so no two (package, publication) pairs can produce
    // the same definition id and every id addresses its own route. Server-generated ids comply.
    internal static bool IsValidIdentifier(string value)
        => value.Length is > 0 and <= MaxIdentifierLength
            && char.IsAsciiLetterOrDigit(value[0])
            && value.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-');

    internal static string IdentifierRule(string field)
        => $"{field} must be 1-{MaxIdentifierLength} characters of letters, digits, '.', '_' or '-', starting with a letter or digit.";

    // Every version, validation, dry-run and publish walks the whole saved graph, so the draft
    // is bounded when it is saved. Returns the first exceeded bound, or null when within limits.
    internal static string? FindExceededLimit(SaveWorkflowPackageRequest request)
    {
        var graph = request.Graph;
        return FirstOver(
            ("name", request.Name.Trim().Length, MaxNameLength),
            ("namespace", request.Namespace?.Trim().Length ?? 0, MaxNameLength),
            ("description", request.Description?.Trim().Length ?? 0, MaxDescriptionLength),
            ("graph.nodes", graph.Nodes?.Count ?? 0, MaxNodes),
            ("graph.edges", graph.Edges.Count, MaxEdges),
            ("metadata", request.Metadata?.Count ?? 0, MaxMetadataEntries),
            ("graph.editorMetadata", graph.EditorMetadata.Count, MaxMetadataEntries));
    }

    private static string? FirstOver(params ReadOnlySpan<(string Field, int Actual, int Limit)> checks)
    {
        foreach (var (field, actual, limit) in checks)
        {
            if (actual > limit)
            {
                return $"{field} exceeds the limit of {limit} (got {actual}).";
            }
        }

        return null;
    }

    // The string enum converter also accepts integers, so {"status": 7} would otherwise bind an
    // undefined value. Switch-based to stay reflection-free under trimming and Native AOT.
    internal static bool IsDefined(WorkflowPublicationStatus value)
        => value is WorkflowPublicationStatus.Active or WorkflowPublicationStatus.Disabled;

    internal static bool IsDefined(WorkflowPublicationTarget value)
        => value is WorkflowPublicationTarget.Job
            or WorkflowPublicationTarget.Schedule
            or WorkflowPublicationTarget.ProcessEndpoint;
}
