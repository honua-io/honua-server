// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;

namespace Honua.Server.Features.WorkflowPackages;

internal static class WorkflowPackageMetadataKeys
{
    public const string PackageId = "workflow.packageId";
    public const string PackageVersion = "workflow.packageVersion";
    public const string PublicationId = "workflow.publicationId";
    public const string PackageHash = "workflow.packageHash";
    public const string Target = "workflow.publicationTarget";
    public const string ProcessId = "workflow.processId";

    /// <summary>
    /// Server-stamped provenance keys that callers must not override on run requests.
    /// Protects traceability of jobs/runs back to the originating package version.
    /// </summary>
    private static readonly FrozenSet<string> ReservedKeys = new[]
    {
        PackageId,
        PackageVersion,
        PublicationId,
        PackageHash,
        Target,
        ProcessId
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="key"/> is a reserved workflow provenance key.
    /// </summary>
    public static bool IsReserved(string key) => ReservedKeys.Contains(key);
}
