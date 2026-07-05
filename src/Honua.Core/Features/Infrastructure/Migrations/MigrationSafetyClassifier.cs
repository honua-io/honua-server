// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.Core.Features.Infrastructure.Migrations;

/// <summary>
/// Expand/contract safety classification for a single database migration script.
/// </summary>
/// <remarks>
/// This encodes the expand/contract (backward-compatibility) discipline from ADR-0060:
/// during a rolling version step two server versions must coexist over one schema, so a
/// migration that removes or narrows schema (a <em>contract</em>-phase change) must be
/// deliberately reviewed and never ride along a rolling deploy. <see cref="Expand"/>
/// changes are additive and always rollout-safe; contract changes are only safe when the
/// author has declared why via the <c>-- honua:compatibility-review reason=&lt;...&gt;</c>
/// marker.
/// </remarks>
public enum MigrationSafetyClassification
{
    /// <summary>
    /// Additive, backward-compatible ("expand") change. Safe to apply alongside a rolling
    /// deploy while an older server version is still serving.
    /// </summary>
    Expand = 0,

    /// <summary>
    /// A potentially backward-incompatible ("contract") change that carries the explicit
    /// <c>honua:compatibility-review</c> marker declaring why it is rollout-safe. Must be
    /// applied in the contract phase (expand → deploy → migrate → contract), not on a
    /// rolling deploy.
    /// </summary>
    ContractAnnotated = 1,

    /// <summary>
    /// A potentially backward-incompatible ("contract") change with no compatibility-review
    /// marker. Rejected by the migration runner (fail closed) because its rollout safety has
    /// not been declared.
    /// </summary>
    ContractUnannotated = 2,
}

/// <summary>
/// Per-script expand/contract classification produced by <see cref="MigrationSafetyClassifier"/>.
/// </summary>
public sealed record MigrationScriptClassification
{
    /// <summary>
    /// The migration script name (as reported by the migration engine).
    /// </summary>
    public required string ScriptName { get; init; }

    /// <summary>
    /// The expand/contract classification for the script.
    /// </summary>
    public required MigrationSafetyClassification Classification { get; init; }

    /// <summary>
    /// The names of the breaking-change rules the script matched (empty for an
    /// <see cref="MigrationSafetyClassification.Expand"/> script).
    /// </summary>
    public IReadOnlyList<string> BreakingRules { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Whether the script contains a potentially backward-incompatible (contract-phase)
    /// change, whether or not it is annotated.
    /// </summary>
    public bool IsBreaking =>
        Classification is MigrationSafetyClassification.ContractAnnotated
            or MigrationSafetyClassification.ContractUnannotated;
}

/// <summary>
/// Classifies database migration scripts as expand (backward-compatible) or contract
/// (potentially backward-incompatible), enforcing the ADR-0060 expand/contract discipline.
/// This is the single source of truth shared by the runtime migration runner, the deploy
/// preflight probe, and the architecture-enforcement test.
/// </summary>
public static class MigrationSafetyClassifier
{
    /// <summary>
    /// The compatibility-review marker convention every contract-phase migration must carry,
    /// shown to authors in enforcement error messages.
    /// </summary>
    public const string CompatibilityReviewMarkerConvention =
        "-- honua:compatibility-review reason=<why this migration is rollout-safe>";

    private static readonly Regex CompatibilityReviewMarker = new(
        @"^\s*--\s*honua:compatibility-review\b.*\breason\s*=",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly (string RuleName, Regex Pattern)[] PotentiallyBreakingPatterns =
    [
        CreatePattern("drop-column", @"\bALTER\s+TABLE\b[\s\S]*?\bDROP\s+COLUMN\b"),
        CreatePattern("rename-column", @"\bALTER\s+TABLE\b[\s\S]*?\bRENAME\s+COLUMN\b"),
        CreatePattern("alter-column-type", @"\bALTER\s+TABLE\b[\s\S]*?\bALTER\s+COLUMN\b[\s\S]*?\bTYPE\b"),
        CreatePattern("set-not-null", @"\bALTER\s+TABLE\b[\s\S]*?\bALTER\s+COLUMN\b[\s\S]*?\bSET\s+NOT\s+NULL\b"),
        CreatePattern("drop-table", @"\bDROP\s+TABLE\b"),
        CreatePattern("drop-schema", @"\bDROP\s+SCHEMA\b"),
        CreatePattern("drop-sequence", @"\bDROP\s+SEQUENCE\b"),
    ];

    /// <summary>
    /// Classifies a single migration script from its name and SQL contents.
    /// </summary>
    /// <param name="scriptName">The migration script name.</param>
    /// <param name="sql">The raw SQL contents of the script.</param>
    /// <returns>The per-script expand/contract classification.</returns>
    public static MigrationScriptClassification Classify(string scriptName, string sql)
    {
        ArgumentNullException.ThrowIfNull(scriptName);
        ArgumentNullException.ThrowIfNull(sql);

        var breakingRules = DetectBreakingRules(sql);

        MigrationSafetyClassification classification;
        if (breakingRules.Count == 0)
        {
            classification = MigrationSafetyClassification.Expand;
        }
        else if (HasCompatibilityReviewMarker(sql))
        {
            classification = MigrationSafetyClassification.ContractAnnotated;
        }
        else
        {
            classification = MigrationSafetyClassification.ContractUnannotated;
        }

        return new MigrationScriptClassification
        {
            ScriptName = scriptName,
            Classification = classification,
            BreakingRules = breakingRules,
        };
    }

    /// <summary>
    /// Returns the names of the potentially-breaking (contract-phase) rules the SQL matches,
    /// after stripping comments and quoted/function bodies. An empty list means the script is
    /// an additive (expand) change.
    /// </summary>
    /// <param name="sql">The raw SQL contents to analyze.</param>
    /// <returns>The matched breaking-rule names, in rule order.</returns>
    public static IReadOnlyList<string> DetectBreakingRules(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var normalized = StripCommentsAndQuotedBodies(sql);
        var matchedRules = new List<string>();

        foreach (var (ruleName, pattern) in PotentiallyBreakingPatterns)
        {
            if (pattern.IsMatch(normalized))
            {
                matchedRules.Add(ruleName);
            }
        }

        return matchedRules;
    }

    /// <summary>
    /// Whether the SQL carries the <c>-- honua:compatibility-review reason=&lt;...&gt;</c>
    /// marker declaring a contract-phase migration as reviewed and rollout-safe.
    /// </summary>
    /// <param name="sql">The raw SQL contents to inspect.</param>
    /// <returns><see langword="true"/> when the marker is present.</returns>
    public static bool HasCompatibilityReviewMarker(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return CompatibilityReviewMarker.IsMatch(sql);
    }

    private static string StripCommentsAndQuotedBodies(string sql)
    {
        var sanitized = Regex.Replace(sql, @"/\*[\s\S]*?\*/", " ", RegexOptions.CultureInvariant);
        sanitized = Regex.Replace(
            sanitized,
            @"\$(?<tag>[A-Za-z_][A-Za-z0-9_]*)?\$[\s\S]*?\$\k<tag>\$",
            " ",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        sanitized = Regex.Replace(
            sanitized,
            @"'([^']|'')*'",
            "''",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        sanitized = Regex.Replace(
            sanitized,
            @"--.*?$",
            string.Empty,
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        return sanitized;
    }

    private static (string RuleName, Regex Pattern) CreatePattern(string ruleName, string pattern) =>
        (ruleName, new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
}
