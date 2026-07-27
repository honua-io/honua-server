// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

/// <summary>
/// Configuration for durable metadata release operation records.
/// </summary>
public sealed class MetadataReleaseOperationOptions
{
    /// <summary>
    /// Configuration section name for metadata release operation settings.
    /// </summary>
    public const string SectionName = "ControlPlane:MetadataRelease";

    /// <summary>
    /// Retention for metadata release operation records and their package-ID index entries.
    /// </summary>
    public TimeSpan OperationRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Demo-only fault-injection controls that deterministically fail the post-publish Smoke health
    /// gate so the reversible-rollback closed loop (metadata reactivation + down-script) can be
    /// exercised and recorded without depending on a naturally broken layer. Safe by default: every
    /// field is inert unless explicitly configured, and injection is refused outside
    /// <see cref="FaultInjection"/>'s allowed-environment list so it can never fire against
    /// production. See <c>runbook/demo-b-safe-rollback.md</c> in the honua-io/honua-demo repository.
    /// </summary>
    public MetadataReleaseFaultInjectionOptions FaultInjection { get; set; } = new();
}

/// <summary>
/// Demo-only fault-injection controls for the metadata-release Smoke health gate. Used to make
/// Demo B's safe-rollback beat deterministic and repeatable. This is not a production capability;
/// it is gated off by default and hard-fenced to non-production target environments.
/// </summary>
public sealed class MetadataReleaseFaultInjectionOptions
{
    /// <summary>
    /// Master switch. When false (default) no fault is ever injected regardless of the other fields.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// When true and <see cref="Enabled"/> is true, the post-publish Smoke check reports failure for
    /// releases whose target environment is in <see cref="AllowedEnvironments"/>. This drives the
    /// reconciler down its existing reversible-rollback path (reactivate prior revision + run the
    /// down-script) so the closed loop can be validated end-to-end.
    /// </summary>
    public bool ForceSmokeFailure { get; set; }

    /// <summary>
    /// Target environments in which fault injection is permitted. Injection is refused for any other
    /// target environment, so a misconfiguration cannot fail a real release. Defaults to the
    /// non-customer-facing demo deploy targets.
    /// </summary>
    public IReadOnlyList<string> AllowedEnvironments { get; set; } = new[] { "staging", "dev", "demo-staging", "demo-dev" };

    /// <summary>
    /// Operator-facing reason recorded on the injected smoke failure so the DevOps AI loop and the
    /// audit trail can see this was a deliberately injected fault.
    /// </summary>
    public string Reason { get; set; } = "Injected smoke failure (Demo B safe-rollback fault injection).";

    /// <summary>
    /// Returns true when an injected smoke failure is permitted for the supplied target environment.
    /// </summary>
    /// <param name="targetEnvironment">The release's target environment.</param>
    public bool ShouldFailSmoke(string? targetEnvironment)
    {
        if (!Enabled || !ForceSmokeFailure || string.IsNullOrWhiteSpace(targetEnvironment))
        {
            return false;
        }

        return AllowedEnvironments.Any(allowed => string.Equals(allowed, targetEnvironment, StringComparison.OrdinalIgnoreCase));
    }
}
