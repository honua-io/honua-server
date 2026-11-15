// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Guardrails.Domain;

namespace Honua.ControlPlane.Executors;

internal enum OpsRemediationTier { Observe, Deterministic, Agent, Human, Never }
internal enum OpsReversibility { Automatic, Manual, None }
internal enum OpsBlastRadius { TaskOrJob, LayerOrService, Deployment }
internal enum OpsDataRisk { None, Recoverable, Irrecoverable }
internal enum OpsCostRisk { None, WithinBound, AboveBound, Commitment }
internal enum OpsSecurityRisk { None, ExposureChange, DisableOrWiden }
internal enum OpsDiagnosis { Deterministic, Inference }

internal sealed record OpsActionDescriptor
{
    public required OpsReversibility Reversibility { get; init; }
    public required OpsBlastRadius BlastRadius { get; init; }
    public required OpsDataRisk DataRisk { get; init; }
    public required OpsCostRisk CostRisk { get; init; }
    public required OpsSecurityRisk SecurityRisk { get; init; }
    public required OpsDiagnosis Diagnosis { get; init; }
    public bool Idempotent { get; init; }
    public bool RateLimited { get; init; }

    public OpsRemediationTier Tier
    {
        get
        {
            if (DataRisk == OpsDataRisk.Irrecoverable || CostRisk == OpsCostRisk.Commitment ||
                SecurityRisk == OpsSecurityRisk.DisableOrWiden)
            {
                return OpsRemediationTier.Never;
            }

            return Reversibility == OpsReversibility.Automatic &&
                BlastRadius is OpsBlastRadius.TaskOrJob or OpsBlastRadius.LayerOrService &&
                DataRisk == OpsDataRisk.None && CostRisk is OpsCostRisk.None or OpsCostRisk.WithinBound &&
                SecurityRisk == OpsSecurityRisk.None && Diagnosis == OpsDiagnosis.Deterministic &&
                Idempotent && RateLimited
                ? OpsRemediationTier.Deterministic
                : OpsRemediationTier.Human;
        }
    }

    public bool IsAutoSafe => Tier == OpsRemediationTier.Deterministic;
    public GuardrailTier GuardrailTier => Tier == OpsRemediationTier.Never
        ? GuardrailTier.Blocked : GuardrailTier.RequiresApproval;
}

internal static class OpsActionCatalog
{
    private static readonly OpsActionDescriptor HumanAction = new()
    {
        Reversibility = OpsReversibility.Manual,
        BlastRadius = OpsBlastRadius.Deployment,
        DataRisk = OpsDataRisk.Recoverable,
        CostRisk = OpsCostRisk.AboveBound,
        SecurityRisk = OpsSecurityRisk.ExposureChange,
        Diagnosis = OpsDiagnosis.Deterministic,
    };

    public static readonly ImmutableDictionary<string, OpsActionDescriptor> Actions =
        new Dictionary<string, OpsActionDescriptor>(StringComparer.Ordinal)
        {
            [OpsActionNames.RedriveDeadLetters] = new()
            {
                // Deliveries already sent to recipients cannot be compensated.
                Reversibility = OpsReversibility.None,
                BlastRadius = OpsBlastRadius.TaskOrJob,
                DataRisk = OpsDataRisk.None,
                CostRisk = OpsCostRisk.None,
                SecurityRisk = OpsSecurityRisk.None,
                Diagnosis = OpsDiagnosis.Deterministic,
                Idempotent = true,
                RateLimited = true,
            },
            [OpsActionNames.PauseChannel] = HumanAction with
            {
                BlastRadius = OpsBlastRadius.LayerOrService,
                DataRisk = OpsDataRisk.None,
                CostRisk = OpsCostRisk.None,
                SecurityRisk = OpsSecurityRisk.None,
            },
            [OpsActionNames.ResumeChannel] = HumanAction with
            {
                BlastRadius = OpsBlastRadius.LayerOrService,
                DataRisk = OpsDataRisk.None,
                CostRisk = OpsCostRisk.None,
                SecurityRisk = OpsSecurityRisk.None,
            },
            [OpsActionNames.TuneBoundedAdmission] = HumanAction with
            {
                BlastRadius = OpsBlastRadius.LayerOrService,
                DataRisk = OpsDataRisk.None,
                CostRisk = OpsCostRisk.None,
                SecurityRisk = OpsSecurityRisk.None,
            },
            [OpsActionNames.ComposeRestartService] = HumanAction with
            {
                DataRisk = OpsDataRisk.None,
                CostRisk = OpsCostRisk.None,
                SecurityRisk = OpsSecurityRisk.None,
            },
            [OpsActionNames.ComposeApplyConfig] = HumanAction,
            [OpsActionNames.RestartEcs] = HumanAction with
            {
                DataRisk = OpsDataRisk.None,
                CostRisk = OpsCostRisk.None,
                SecurityRisk = OpsSecurityRisk.None,
            },
            [OpsActionNames.RestartLambda] = HumanAction with
            {
                DataRisk = OpsDataRisk.None,
                CostRisk = OpsCostRisk.None,
                SecurityRisk = OpsSecurityRisk.None,
            },
            [OpsActionNames.SetConfig] = HumanAction,
            ["data.delete"] = HumanAction with { DataRisk = OpsDataRisk.Irrecoverable },
            ["backup.delete"] = HumanAction with { DataRisk = OpsDataRisk.Irrecoverable },
            ["security.disable"] = HumanAction with { SecurityRisk = OpsSecurityRisk.DisableOrWiden },
            ["iam.widen"] = HumanAction with { SecurityRisk = OpsSecurityRisk.DisableOrWiden },
            ["network.widen"] = HumanAction with { SecurityRisk = OpsSecurityRisk.DisableOrWiden },
            ["billing.commit"] = HumanAction with { CostRisk = OpsCostRisk.Commitment },
        }.ToImmutableDictionary(StringComparer.Ordinal);

    public static readonly ImmutableDictionary<string, GuardrailTier> GuardrailTiers =
        Actions.ToImmutableDictionary(static action => action.Key,
            static action => action.Value.GuardrailTier, StringComparer.Ordinal);

    public static bool IsRegistered(string action) => Actions.ContainsKey(action);
}
