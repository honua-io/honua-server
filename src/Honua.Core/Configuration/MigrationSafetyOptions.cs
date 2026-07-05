// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Configuration;

/// <summary>
/// Controls enforcement of the expand/contract migration-safety gate (ADR-0060, principle #3a).
/// </summary>
/// <remarks>
/// When <see cref="Enforce"/> is <see langword="true"/> (the default), the migration runner
/// fails closed if any pending script contains a potentially backward-incompatible ("contract")
/// change that is not annotated with the
/// <c>-- honua:compatibility-review reason=&lt;...&gt;</c> marker. The default is safe because
/// every existing breaking migration is already annotated; the escape hatch
/// (<c>Database:MigrationSafety:Enforce=false</c>) exists only for operators who must apply an
/// unreviewed contract migration deliberately and accept the rolling-upgrade risk.
/// </remarks>
public sealed record MigrationSafetyOptions
{
    /// <summary>
    /// Configuration section name for binding from <c>appsettings</c>/environment.
    /// </summary>
    public const string SectionName = "Database:MigrationSafety";

    /// <summary>
    /// When <see langword="true"/> (default), the migration runner rejects pending contract-phase
    /// migrations that lack the compatibility-review marker instead of applying them.
    /// </summary>
    public bool Enforce { get; init; } = true;
}
