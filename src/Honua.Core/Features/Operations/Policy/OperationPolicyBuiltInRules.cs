// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Policy;

/// <summary>
/// Code-declared operation policy rules that the Production environment appends after the
/// operator-configured <see cref="OperationPolicyOptions.Rules"/>. They let a Production install
/// work without operator-authored rules: full administrators (<c>admin</c>) and administrative
/// API keys (<c>scoped-admin-key</c>) execute typed operations directly, while every other caller
/// still falls through to the fail-closed <see cref="OperationPolicyOptions.DefaultDecision"/>.
/// </summary>
/// <remarks>
/// The rules live in code rather than <c>appsettings.Production.json</c> so an operator's own
/// configuration cannot silently drop them. Operator rules are evaluated first, so an operator can
/// still deny administrators or require approval for them. Approved-operation credentials
/// (<c>approved-operation</c>) need no rule: they are minted only for the replay of an approved
/// proposal, whose dispatch skips the policy decision point, and they call the admin REST surface
/// bound to a single method and path rather than the typed operation runtime.
/// </remarks>
public static class OperationPolicyBuiltInRules
{
    /// <summary>Role carried by full administrators (bootstrap password admin, <c>admin</c>/<c>*</c>/<c>admin:*</c>/<c>admin:write</c>/<c>admin:manage</c> keys).</summary>
    public const string FullAdministratorRole = "admin";

    /// <summary>Role carried by API keys with narrower administrative grants such as <c>admin:read</c> or <c>admin:approve</c>.</summary>
    public const string AdministrativeKeyRole = "scoped-admin-key";

    /// <summary>Reason carried by a decision produced by the full-administrator built-in rule.</summary>
    public const string FullAdministratorReason = "built-in: full administrators execute directly";

    /// <summary>Reason carried by a decision produced by the administrative-key built-in rule.</summary>
    public const string AdministrativeKeyReason = "built-in: administrative keys execute directly";

    /// <summary>
    /// The built-in Production rules, in evaluation order. Each call returns fresh instances so a
    /// caller cannot mutate the shared definitions.
    /// </summary>
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

    /// <summary>
    /// Composes the effective rule set for <paramref name="options"/> in the given environment:
    /// operator rules, then the built-in Production rules when they apply.
    /// </summary>
    /// <param name="options">The bound policy options.</param>
    /// <param name="productionEnvironment">Whether the host runs in the Production environment.</param>
    /// <returns>The effective, ordered policy composition.</returns>
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

/// <summary>
/// The effective operation policy: the operator rules followed by any built-in rules, then the
/// default decision.
/// </summary>
/// <param name="Enabled">Whether the policy is enforced at all.</param>
/// <param name="Rules">The ordered, first-match-wins effective rules.</param>
/// <param name="OperatorRuleCount">How many leading rules came from operator configuration.</param>
/// <param name="BuiltInRuleCount">How many trailing rules are code-declared built-ins.</param>
/// <param name="DefaultDecision">The decision applied when no rule matches.</param>
public sealed record OperationPolicyComposition(
    bool Enabled,
    IReadOnlyList<OperationPolicyRule> Rules,
    int OperatorRuleCount,
    int BuiltInRuleCount,
    PolicyDecisionKind DefaultDecision)
{
    /// <summary>Whether the built-in Production rules are part of the effective rule set.</summary>
    public bool BuiltInRules => BuiltInRuleCount > 0;

    /// <summary>
    /// A one-line operator-facing summary, for example
    /// <c>0 operator rules + 2 built-in rules, default Deny</c>.
    /// </summary>
    /// <returns>The summary text.</returns>
    public string Describe() => Enabled
        ? $"{OperatorRuleCount} operator rules + {BuiltInRuleCount} built-in rules, default {DefaultDecision}"
        : "disabled (pass-through allow)";
}
