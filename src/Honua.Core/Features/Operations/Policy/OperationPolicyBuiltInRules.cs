// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Policy;

// Operator rules are evaluated first and can deny an administrator or require approval. These Production allows live in code so a configuration replacement cannot drop them.
public static class OperationPolicyBuiltInRules
{
    public const string FullAdministratorRole = "admin";

    public const string AdministrativeKeyRole = "scoped-admin-key";

    public const string FullAdministratorReason = "built-in: full administrators execute directly";

    public const string AdministrativeKeyReason = "built-in: administrative keys execute directly";

    // Each call returns fresh rule instances so a caller cannot mutate the shared definitions.
    public static IReadOnlyList<OperationPolicyRule> Production =>
    [
        new()
        {
            OperationId = "*",
            Role = FullAdministratorRole,
            Decision = PolicyDecisionKind.Allow,
            Reason = FullAdministratorReason,
        },
        new()
        {
            OperationId = "*",
            Role = AdministrativeKeyRole,
            Decision = PolicyDecisionKind.Allow,
            Reason = AdministrativeKeyReason,
        },
    ];

    public static OperationPolicyComposition Compose(OperationPolicyOptions options, bool productionEnvironment)
    {
        ArgumentNullException.ThrowIfNull(options);
        var operatorRules = options.Rules.Where(static rule => rule is not null).ToArray();
        var builtIns = productionEnvironment && options.Enabled && options.BuiltInProductionRules
            ? Production
            : [];
        return new OperationPolicyComposition(
            options.Enabled,
            [.. operatorRules, .. builtIns],
            operatorRules.Length,
            builtIns.Count,
            options.DefaultDecision);
    }
}

public sealed record OperationPolicyComposition(
    bool Enabled,
    IReadOnlyList<OperationPolicyRule> Rules,
    int OperatorRuleCount,
    int BuiltInRuleCount,
    PolicyDecisionKind DefaultDecision)
{
    public bool BuiltInRules => BuiltInRuleCount > 0;

    public string Describe() => Enabled
        ? $"{OperatorRuleCount} operator rules + {BuiltInRuleCount} built-in rules, default {DefaultDecision}"
        : "disabled (pass-through allow)";
}
