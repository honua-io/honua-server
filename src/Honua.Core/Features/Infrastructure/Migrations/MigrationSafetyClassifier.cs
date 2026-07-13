// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
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

    // The reason must be bound to a non-empty reviewer/ticket identity: an empty `reason=` (or one that
    // is only whitespace to end-of-line) is self-service theater that lets a schema-narrowing migration
    // ride along without an actual justification, so it is NOT treated as an annotated marker. Requiring
    // a non-whitespace token after `reason=` forces the author to record why the contract change is
    // rollout-safe (ADR-0060; honua-server#2812).
    // The non-whitespace reason token must sit on the SAME line as `reason=`: only horizontal whitespace
    // ([ \t]) is allowed around the `=` and before the token, so an empty `reason=` at end-of-line does
    // not spuriously "reach" the next line's DDL (e.g. ALTER) and count as a filled-in reason.
    private static readonly Regex CompatibilityReviewMarker = new(
        @"^\s*--\s*honua:compatibility-review\b.*\breason[ \t]*=[ \t]*\S",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly (string RuleName, Regex Pattern)[] PotentiallyBreakingPatterns =
    [
        CreatePattern("drop-column", @"\bALTER\s+TABLE\b[\s\S]*?\bDROP\s+COLUMN\b"),
        CreatePattern("rename-column", @"\bALTER\s+TABLE\b[\s\S]*?\bRENAME\s+COLUMN\b"),
        CreatePattern("rename-table", @"\bALTER\s+TABLE\b[\s\S]*?\bRENAME\s+TO\b"),
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

    // A single left-to-right lexer pass that removes comments and quoted/dollar-quoted bodies. A
    // regex-per-construct approach is order-sensitive and unsafe: stripping string literals before
    // line comments lets an apostrophe inside a `--` comment (e.g. "-- don't ship" ... "-- isn't used")
    // open a spurious quote span that swallows real DDL between the two comments, hiding a DROP TABLE
    // and misclassifying a contract migration as expand. A lexer honors the real precedence — a `--`
    // or `/* */` outside a string starts a comment, and a `'`/`$tag$` outside a comment starts a
    // literal — so no construct can leak across another's boundary.
    private static string StripCommentsAndQuotedBodies(string sql)
    {
        var result = new StringBuilder(sql.Length);
        var i = 0;
        var length = sql.Length;

        while (i < length)
        {
            var current = sql[i];

            // Line comment: -- to end of line.
            if (current == '-' && i + 1 < length && sql[i + 1] == '-')
            {
                i += 2;
                while (i < length && sql[i] != '\n')
                {
                    i++;
                }

                result.Append(' ');
                continue;
            }

            // Block comment: /* ... */ (Postgres does not nest these by default; a flat scan matches
            // how the classifier's DDL detection needs it).
            if (current == '/' && i + 1 < length && sql[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < length && !(sql[i] == '*' && sql[i + 1] == '/'))
                {
                    i++;
                }

                i = Math.Min(length, i + 2);
                result.Append(' ');
                continue;
            }

            // Single-quoted string literal with '' escape.
            if (current == '\'')
            {
                i++;
                while (i < length)
                {
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < length && sql[i + 1] == '\'')
                        {
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    i++;
                }

                result.Append(' ');
                continue;
            }

            // Dollar-quoted body: $tag$ ... $tag$ (tag may be empty).
            if (current == '$' && TryReadDollarTag(sql, i) is { } tag)
            {
                var closeIndex = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                i = closeIndex < 0 ? length : closeIndex + tag.Length;
                result.Append(' ');
                continue;
            }

            result.Append(current);
            i++;
        }

        return result.ToString();
    }

    private static string? TryReadDollarTag(string sql, int start)
    {
        var length = sql.Length;
        var i = start + 1;

        // Empty tag: $$.
        if (i < length && sql[i] == '$')
        {
            return "$$";
        }

        // A named tag must be a valid identifier (letter/underscore first) so a bare parameter
        // reference such as $1 is not mistaken for the opening of a dollar-quoted body.
        if (i >= length || !(char.IsAsciiLetter(sql[i]) || sql[i] == '_'))
        {
            return null;
        }

        i++;
        while (i < length && (char.IsAsciiLetterOrDigit(sql[i]) || sql[i] == '_'))
        {
            i++;
        }

        return i < length && sql[i] == '$' ? sql[start..(i + 1)] : null;
    }

    /// <summary>
    /// Computes the one-shot contract-apply approval nonce for a specific set of pending contract-phase
    /// migration scripts. The nonce is a stable digest bound to the exact script names, so an approval
    /// token is only ever valid for the migrations it was minted for: once those scripts are applied they
    /// leave the pending set (they are journaled), and the digest for any later contract migration
    /// differs, so a stale <c>HONUA_APPROVE_CONTRACT_MIGRATIONS</c> value can never silently approve an
    /// unrelated future contract change (honua-server#2812).
    /// </summary>
    /// <param name="scriptNames">The pending contract-phase migration script names to bind the nonce to.</param>
    /// <returns>A lowercase hexadecimal nonce, or an empty string when no scripts are supplied.</returns>
    public static string ComputeContractApprovalNonce(IEnumerable<string> scriptNames)
    {
        ArgumentNullException.ThrowIfNull(scriptNames);

        var ordered = scriptNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        if (ordered.Length == 0)
        {
            return string.Empty;
        }

        var payload = Encoding.UTF8.GetBytes(string.Join('\n', ordered));
        var hash = SHA256.HashData(payload);

        // 16 hex chars (64 bits) is ample to bind an approval to a specific script set while staying
        // short enough for an operator to copy from the block message into an environment variable.
        return Convert.ToHexStringLower(hash)[..16];
    }

    private static (string RuleName, Regex Pattern) CreatePattern(string ruleName, string pattern) =>
        (ruleName, new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
}
