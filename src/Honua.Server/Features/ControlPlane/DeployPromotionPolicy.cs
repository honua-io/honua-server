// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

/// <summary>
/// How a coordinated deploy reaches the promotion (cutover) step. Distinct from the telemetry
/// <em>rollback</em> gate: this decides what must be satisfied before the reconciler auto-promotes a
/// healthy rollout.
/// </summary>
internal enum DeployPromotionGateMode
{
    /// <summary>
    /// Promote only after a configured metrics telemetry gate passes. This is the cloud default and
    /// preserves the historical behavior: without a reachable telemetry backend the deploy holds in
    /// <c>Reconciling</c> until an operator forces promotion.
    /// </summary>
    Telemetry,

    /// <summary>
    /// Promote on the backend's own health gate alone (for example the self-hosted YARP rolling
    /// backend, which health-probes the standby replica locally before recommending promotion). A
    /// metrics telemetry backend is optional; when one is configured its rollback signals still
    /// apply and its pending waits still hold, but no metrics pass is required to promote. This is the
    /// on-prem/air-gapped default so a cutover no longer requires a cloud-style Prometheus.
    /// </summary>
    Health,

    /// <summary>
    /// Never auto-promote. The rollout bakes and holds until an operator explicitly promotes it
    /// through the admin promote endpoint. Rollback signals still fire automatically.
    /// </summary>
    Manual
}

/// <summary>
/// Resolves the <see cref="DeployPromotionGateMode"/> for a deploy operation from its parameters,
/// with a target-kind-aware default posture.
/// </summary>
internal static class DeployPromotionPolicy
{
    /// <summary>
    /// Deploy-spec parameter key an operator sets to choose the promotion gate explicitly. Accepts
    /// <c>telemetry</c>, <c>health</c> (alias <c>health-only</c>), or <c>manual</c>. When unset the
    /// gate defaults by target kind (see <see cref="Resolve"/>).
    /// </summary>
    internal const string PromotionGateParameterKey = "deployment.promotion_gate";

    /// <summary>
    /// Resolves the promotion gate for the supplied deploy spec. An explicit
    /// <see cref="PromotionGateParameterKey"/> wins; otherwise self-hosted rolling deploys default to
    /// <see cref="DeployPromotionGateMode.Health"/> (no cloud metrics substrate exists on-prem) and
    /// every other target kind defaults to <see cref="DeployPromotionGateMode.Telemetry"/>, preserving
    /// the strict cloud gate.
    /// </summary>
    public static DeployPromotionGateMode Resolve(DeployOperationSpec? spec)
    {
        if (spec == null)
        {
            return DeployPromotionGateMode.Telemetry;
        }

        if (spec.Parameters.TryGetValue(PromotionGateParameterKey, out var raw) &&
            !string.IsNullOrWhiteSpace(raw))
        {
            switch (raw.Trim().ToLowerInvariant())
            {
                case "telemetry":
                    return DeployPromotionGateMode.Telemetry;
                case "health":
                case "health-only":
                    return DeployPromotionGateMode.Health;
                case "manual":
                    return DeployPromotionGateMode.Manual;
                default:
                    // An unrecognized value falls back to the kind-aware default below rather than
                    // failing the deploy outright.
                    break;
            }
        }

        return spec.TargetKind == DeployTargetKind.SelfHostedRolling
            ? DeployPromotionGateMode.Health
            : DeployPromotionGateMode.Telemetry;
    }
}
