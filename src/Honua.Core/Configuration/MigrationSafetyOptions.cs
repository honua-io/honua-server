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
    /// Apply annotated contract-phase migrations automatically at boot — today's behavior. Annotated
    /// scripts have already passed the compatibility-review gate, so an unattended upgrade applies them.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// On an existing database, refuse to apply pending annotated contract-phase migrations unless the
    /// operator has explicitly approved them (<c>HONUA_APPROVE_CONTRACT_MIGRATIONS=true</c>). Fresh
    /// installs (empty journal) are unaffected and always provision fully. This turns a single-node
    /// image pull into a reviewed, deliberate step for schema-narrowing changes rather than an
    /// unattended one.
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
/// an existing database. Under <see cref="Core.Configuration.ContractApplyPolicy.Gate"/> they require an
/// explicit operator approval (<c>HONUA_APPROVE_CONTRACT_MIGRATIONS=true</c>); the gate applies only when
/// the migration journal is non-empty, so fresh installs always provision fully with zero configuration.
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
    /// Configuration key (top-level environment variable) an operator sets to <c>true</c> to approve
    /// applying pending annotated contract-phase migrations while
    /// <see cref="ContractApplyPolicy"/> is <see cref="Core.Configuration.ContractApplyPolicy.Gate"/>.
    /// </summary>
    public const string ApproveContractMigrationsKey = "HONUA_APPROVE_CONTRACT_MIGRATIONS";

    /// <summary>
    /// When <see langword="true"/> (default), the migration runner rejects pending contract-phase
    /// migrations that lack the compatibility-review marker instead of applying them.
    /// </summary>
    public bool Enforce { get; init; } = true;

    /// <summary>
    /// Policy for applying pending <em>annotated</em> contract-phase migrations on an existing database
    /// (non-empty journal). Defaults to <see cref="Core.Configuration.ContractApplyPolicy.Auto"/>, which
    /// preserves today's behavior. Fresh installs always provision fully regardless of this setting.
    /// </summary>
    public ContractApplyPolicy ContractApplyPolicy { get; init; } = ContractApplyPolicy.Auto;

    /// <summary>
    /// Optional pre-migration backup command. When set, the runner executes it via the platform shell
    /// immediately before applying pending contract-class scripts on an existing database (non-empty
    /// journal). A non-zero exit fails the migration run closed (no script is applied). This value is
    /// configuration-source only and is never writable through an admin API or the database (RCE guard).
    /// A typical value is a <c>pg_dump</c> invocation.
    /// </summary>
    public string? BackupCommand { get; init; }
}
