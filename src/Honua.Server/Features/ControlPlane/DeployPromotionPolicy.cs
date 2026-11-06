// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

internal enum DeployPromotionGateMode
{
    Telemetry,

    // Health-only promotion does not require a metrics pass; configured rollback signals and pending waits still apply.
    Health,

    // The rollout holds until an operator promotes it, and rollback signals still fire.
    Manual
}

internal static class DeployPromotionPolicy
{
    internal const string PromotionGateParameterKey = "deployment.promotion_gate";

    // An explicit gate wins; otherwise self-hosted rolling and local Compose default to health-only, and every other target stays on the telemetry gate.
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
                    // Plan-time Validate rejects an unrecognized value (#4617); this fallback only
                    // applies to operations persisted before that validation existed.
                    break;
            }
        }

        return spec.TargetKind is DeployTargetKind.SelfHostedRolling or DeployTargetKind.LocalCompose
            ? DeployPromotionGateMode.Health
            : DeployPromotionGateMode.Telemetry;
    }

    // An unrecognized promotion gate blocks the plan so a typo cannot fall back to a different gate.
    public static string? Validate(DeployOperationSpec spec)
    {
        if (!spec.Parameters.TryGetValue(PromotionGateParameterKey, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim().ToLowerInvariant() is "telemetry" or "health" or "health-only" or "manual"
            ? null
            : $"{PromotionGateParameterKey} '{raw.Trim()}' is not recognized; use telemetry, health or manual.";
    }
}
