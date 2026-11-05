// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

    public const string RunParameterPrefix = "workflow.parameter.";

    // Callers must not override these provenance keys on a run request.
    private static readonly FrozenSet<string> ReservedKeys = new[]
    {
        PackageId,
        PackageVersion,
        PublicationId,
        PackageHash,
        Target,
        ProcessId
    }.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsReserved(string key) => ReservedKeys.Contains(key);
}
