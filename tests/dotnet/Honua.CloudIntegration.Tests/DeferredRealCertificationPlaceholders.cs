// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Real-AWS certification lane (#2164) — intentionally-deferred cells, kept as explicit skipped
/// placeholders so the matrix documents WHY they are absent rather than silently under-covering.
/// Both always skip regardless of lane state; they exist for traceability and to reserve the cell
/// name for the follow-up that implements them.
/// </summary>
[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.RealAwsCertification)]
public sealed class DeferredRealCertificationPlaceholders
{
    /// <summary>
    /// DEFERRED (#2161): failure-of-failure certification — asserting the control plane's behaviour
    /// when a rollback or teardown itself fails (double-fault handling). Implementing it live needs a
    /// fault-injection surface on standing infrastructure whose complexity is not justified now; the
    /// double-fault reconciliation paths are covered by unit/integration tests today (for example
    /// <c>DeployWorkflowReconcilerRollbackFailureTests</c>). Reserved here so the live matrix records
    /// the gap and points at the owning issue.
    /// </summary>
    [SkippableFact]
    public void FailureOfFailure_Certification_IsDeferred()
    {
        Skip.If(
            true,
            "Deferred to #2161: live failure-of-failure (double-fault) certification is intentionally "
            + "not implemented; the reconciliation paths are unit/integration-tested. This placeholder "
            + "reserves the cell and documents the deferral.");
    }
}
