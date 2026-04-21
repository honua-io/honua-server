// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Spec.Domain;

/// <summary>
/// Stable diagnostic codes surfaced by the plan / apply engine. Kept stable
/// because admin tooling and operator workflows key off these strings.
/// </summary>
public static class SpecDiagnosticCodes
{
    /// <summary>Operation expects projected CRS but input is geographic (or vice versa).</summary>
    public const string CrsMismatch = "crs-mismatch";

    /// <summary>Spec references a column not present in the catalog.</summary>
    public const string MissingColumn = "missing-column";

    /// <summary>An <c>@</c> reference does not resolve against the current catalog.</summary>
    public const string UnknownService = "unknown-service";

    /// <summary>Source is mutable and not pinned; cache degrades to TTL.</summary>
    public const string MutableSourceNoPin = "mutable-source-no-pin";

    /// <summary>Operator principal cannot read one or more sources.</summary>
    public const string RbacOutOfScope = "rbac-out-of-scope";

    /// <summary>Grammar or process-family version is outside server support range.</summary>
    public const string VersionSkew = "version-skew";

    /// <summary>Estimated bytes exceed an operator-configured threshold.</summary>
    public const string EstimatedOversize = "estimated-oversize";

    /// <summary>Operator uses an op flagged as non-pure (e.g. sample-based).</summary>
    public const string NondeterministicOp = "nondeterministic-op";

    /// <summary>Node kind is reserved but not yet implemented in the current stage.</summary>
    public const string SpecKindNotInS1 = "spec-kind-not-in-s1";

    /// <summary>DAG declares a cycle; plan cannot be constructed.</summary>
    public const string DagCycle = "dag-cycle";

    /// <summary>Duplicate node identifier declared within the spec.</summary>
    public const string DuplicateNodeId = "duplicate-node-id";

    /// <summary>Unresolved node reference — dependency points to a missing id.</summary>
    public const string UnresolvedReference = "unresolved-reference";

    /// <summary>Request body could not be parsed as a canonical spec document.</summary>
    public const string InvalidRequestBody = "invalid-request-body";

    /// <summary>Apply token is unknown — most commonly because the server restarted.</summary>
    public const string ApplyTokenUnknown = "apply-token-unknown";

    /// <summary>Artifact hash is unknown or has been evicted from the cache.</summary>
    public const string ArtifactNotFound = "artifact-not-found";

    /// <summary>Input compute node failed; downstream nodes are not attempted.</summary>
    public const string UpstreamFailed = "upstream-failed";

    /// <summary>Apply was cancelled via <c>POST /v1/spec/cancel</c>.</summary>
    public const string ApplyCancelled = "apply-cancelled";

    /// <summary>Cache miss encountered while applying in <c>ReadOnly</c> mode.</summary>
    public const string ReadOnlyCacheMiss = "read-only-cache-miss";

    /// <summary>Node declared without a resource kind (REST omitted field, gRPC <c>SPEC_RESOURCE_KIND_UNSPECIFIED</c>).</summary>
    public const string UnknownKind = "unknown-kind";
}
