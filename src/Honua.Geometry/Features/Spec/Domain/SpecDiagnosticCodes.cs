// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Spec.Domain;

public static class SpecDiagnosticCodes
{
    public const string CrsMismatch = "crs-mismatch";

    public const string MissingColumn = "missing-column";

    public const string UnknownService = "unknown-service";

    public const string MutableSourceNoPin = "mutable-source-no-pin";

    public const string RbacOutOfScope = "rbac-out-of-scope";

    public const string VersionSkew = "version-skew";

    public const string EstimatedOversize = "estimated-oversize";

    public const string NondeterministicOp = "nondeterministic-op";

    public const string SpecKindNotInS1 = "spec-kind-not-in-s1";

    public const string DagCycle = "dag-cycle";

    public const string DuplicateNodeId = "duplicate-node-id";

    public const string UnresolvedReference = "unresolved-reference";

    public const string InvalidRequestBody = "invalid-request-body";

    public const string ApplyTokenUnknown = "apply-token-unknown";

    public const string ArtifactNotFound = "artifact-not-found";

    public const string UpstreamFailed = "upstream-failed";

    public const string ApplyCancelled = "apply-cancelled";

    public const string ReadOnlyCacheMiss = "read-only-cache-miss";

    public const string UnknownKind = "unknown-kind";

    public const string UnknownCacheMode = "unknown-cache-mode";

    public const string InvalidNodeId = "invalid-node-id";
}
