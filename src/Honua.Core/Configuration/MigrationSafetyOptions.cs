// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Configuration;

/// <summary>
/// Governs how pending annotated (reviewed) contract-phase migrations are applied when the migration
/// runner boots against an <em>existing</em> database (non-empty migration journal).
/// </summary>
public enum ContractApplyPolicy
{
    /// <summary>
    /// Apply annotated contract-phase migrations automatically at boot. Annotated scripts have passed the
    /// compatibility-review gate, so an unattended upgrade applies them. This is <em>not</em> the default:
    /// under a rolling deploy the first upgraded node applies a schema-narrowing DROP while N-1 old nodes
    /// are still serving, so <see cref="Gate"/> is the safe default and <c>Auto</c> is an explicit opt-out.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// On an existing database, refuse to apply pending annotated contract-phase migrations unless the
    /// operator has explicitly approved them with a one-shot nonce
    /// (<c>HONUA_APPROVE_CONTRACT_MIGRATIONS=&lt;nonce&gt;</c>, printed in the block message and bound to the
    /// exact pending scripts). Fresh installs (empty journal) are unaffected and always provision fully.
    /// This is the default: it turns a single-node image pull into a reviewed, deliberate step for
    /// schema-narrowing changes rather than an unattended one that would break a rolling upgrade.
    /// </summary>
    Gate = 1,
}

/// <summary>
/// Controls enforcement of the expand/contract migration-safety gate (ADR-0060, principle #3a) and the
/// journal-scoped contract-apply policy for safe single-node upgrades (#2565).
/// </summary>
/// <remarks>
/// <para>
/// When <see cref="Enforce"/> is <see langword="true"/> (the default), the migration runner
/// fails closed if any pending script contains a potentially backward-incompatible ("contract")
/// change that is not annotated with the
/// <c>-- honua:compatibility-review reason=&lt;...&gt;</c> marker. The default is safe because
/// every existing breaking migration is already annotated; the escape hatch
/// (<c>Database:MigrationSafety:Enforce=false</c>) exists only for operators who must apply an
/// unreviewed contract migration deliberately and accept the rolling-upgrade risk.
/// </para>
/// <para>
/// <see cref="ContractApplyPolicy"/> governs how <em>annotated</em> contract migrations are applied on
/// an existing database and defaults to <see cref="Core.Configuration.ContractApplyPolicy.Gate"/>, under
/// which they require an explicit one-shot operator approval
/// (<c>HONUA_APPROVE_CONTRACT_MIGRATIONS=&lt;nonce&gt;</c>); the gate applies only when the migration journal
/// is non-empty, so fresh installs always provision fully with zero configuration.
/// </para>
/// <para>
/// <see cref="BackupCommand"/> is an optional pre-migration backup hook run just before contract-class
/// scripts are applied. It is <strong>configuration-source only</strong> (bound once from
/// appsettings/environment via <see cref="Microsoft.Extensions.Options.IOptions{TOptions}"/>) and is never
/// settable through any admin API or database path — the runner shells out to it, so an API/DB-writable
/// value would be a remote-code-execution vector.
/// </para>
/// </remarks>
public sealed record MigrationSafetyOptions
{
    /// <summary>
    /// Configuration section name for binding from <c>appsettings</c>/environment.
    /// </summary>
    public const string SectionName = "Database:MigrationSafety";

    /// <summary>
    /// Configuration key (top-level environment variable) an operator sets to the one-shot approval nonce
    /// to authorize applying pending annotated contract-phase migrations while
    /// <see cref="ContractApplyPolicy"/> is <see cref="Core.Configuration.ContractApplyPolicy.Gate"/>. The
    /// nonce is printed in the block message and is bound (via
    /// <see cref="Features.Infrastructure.Migrations.MigrationSafetyClassifier.ComputeContractApprovalNonce"/>)
    /// to the exact pending scripts, so it approves only those migrations and cannot silently approve a
    /// later contract change (honua-server#2812).
    /// </summary>
    public const string ApproveContractMigrationsKey = "HONUA_APPROVE_CONTRACT_MIGRATIONS";

    /// <summary>
    /// When <see langword="true"/> (default), the migration runner rejects pending contract-phase
    /// migrations that lack the compatibility-review marker instead of applying them.
    /// </summary>
    public bool Enforce { get; set; } = true;

    /// <summary>
    /// Policy for applying pending <em>annotated</em> contract-phase migrations on an existing database
    /// (non-empty journal). Defaults to <see cref="Core.Configuration.ContractApplyPolicy.Gate"/> so a
    /// schema-narrowing migration is never applied unattended by the first upgraded node while older
    /// nodes are still serving (honua-server#2812); set
    /// <see cref="Core.Configuration.ContractApplyPolicy.Auto"/> to opt back into unattended apply. Fresh
    /// installs always provision fully regardless of this setting.
    /// </summary>
    public ContractApplyPolicy ContractApplyPolicy { get; set; } = ContractApplyPolicy.Gate;

    /// <summary>
    /// Optional pre-migration backup command. When set, the runner executes it via the platform shell
    /// immediately before applying pending contract-class scripts on an existing database (non-empty
    /// journal). A non-zero exit fails the migration run closed (no script is applied). This value is
    /// configuration-source only and is never writable through an admin API or the database (RCE guard).
    /// A typical value is a <c>pg_dump</c> invocation.
    /// </summary>
    public string? BackupCommand { get; set; }
}
