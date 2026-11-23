// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.WorkflowPackages;

// Bounds on what a client may send when it names or saves a workflow package.
internal static class WorkflowPackageRequestLimits
{
    internal const int MaxIdentifierLength = 128;

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
}
