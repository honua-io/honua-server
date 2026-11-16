// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Honua.Core.Features.Infrastructure.Migrations;

public enum MigrationSafetyClassification
{
    Expand = 0,

    ContractAnnotated = 1,

    ContractUnannotated = 2,
}

public sealed record MigrationScriptClassification
{
    public required string ScriptName { get; init; }

    public required MigrationSafetyClassification Classification { get; init; }

    public IReadOnlyList<string> BreakingRules { get; init; } = Array.Empty<string>();

    public bool IsBreaking =>
        Classification is MigrationSafetyClassification.ContractAnnotated
            or MigrationSafetyClassification.ContractUnannotated;
}

public static class MigrationSafetyClassifier
{
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

    // A declared contract-phase marker remains a separate finding for migrations whose executable
    // behavior is intentionally broader than these lexical DDL rules.
    private static readonly Regex DeclaredContractPhaseMarker = new(
        @"^\s*--\s*honua:migration-phase[ \t]+contract\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly (string RuleName, Regex Pattern)[] PotentiallyBreakingPatterns =
    [
        CreatePattern("drop-column", @"\bALTER\s+(?:FOREIGN\s+)?TABLE\b[^;]*?\bDROP\s+COLUMN\b"),
        CreatePattern("rename-column", @"\bALTER\s+(?:FOREIGN\s+)?TABLE\b[^;]*?\bRENAME\s+COLUMN\b"),
        CreatePattern("rename-table", @"\bALTER\s+(?:FOREIGN\s+)?TABLE\b[^;]*?\bRENAME\s+TO\b"),
        CreatePattern("rename-index", @"\bALTER\s+INDEX\b[^;]*?\bRENAME\s+TO\b"),
        CreatePattern("alter-column-type", @"\bALTER\s+(?:FOREIGN\s+)?TABLE\b[^;]*?\bALTER\s+COLUMN\b[^;]*?\bTYPE\b"),
        CreatePattern("set-not-null", @"\bALTER\s+(?:FOREIGN\s+)?TABLE\b[^;]*?\bALTER\s+COLUMN\b[^;]*?\bSET\s+NOT\s+NULL\b"),
        CreatePattern("drop-table", @"\bDROP\s+TABLE\b"),
        CreatePattern("drop-schema", @"\bDROP\s+SCHEMA\b"),
        CreatePattern("drop-sequence", @"\bDROP\s+SEQUENCE\b"),
        CreatePattern("drop-view", @"\bDROP\s+(?:MATERIALIZED\s+)?VIEW\b"),
        CreatePattern("drop-type", @"\bDROP\s+(?:TYPE|DOMAIN)\b"),
        CreatePattern("drop-routine", @"\bDROP\s+(?:FUNCTION|PROCEDURE|ROUTINE|AGGREGATE)\b"),
        CreatePattern("drop-trigger", @"\bDROP\s+(?:EVENT\s+)?TRIGGER\b"),
        CreatePattern("drop-policy", @"\bDROP\s+(?:POLICY|RULE)\b"),
        CreatePattern("drop-index", @"\bDROP\s+INDEX\b"),
        CreatePattern("drop-other-object", @"\bDROP\s+(?!COLUMN\b|TABLE\b|SCHEMA\b|SEQUENCE\b|(?:MATERIALIZED\s+)?VIEW\b|TYPE\b|DOMAIN\b|FUNCTION\b|PROCEDURE\b|ROUTINE\b|AGGREGATE\b|(?:EVENT\s+)?TRIGGER\b|POLICY\b|RULE\b|INDEX\b|NOT\s+NULL\b)[A-Z_""%]"),
        CreatePattern("rename-object", @"\bALTER\s+(?!(?:FOREIGN\s+)?TABLE\b|INDEX\b)\S[^;]*?\bRENAME\b"),
        CreatePattern("move-object-schema", @"\bALTER\s+[^;]*?\bSET\s+SCHEMA\b"),
        CreatePattern("truncate-table", @"\bTRUNCATE\b"),
    ];

    private static readonly Lazy<Dictionary<string, string>> FrozenReaderMigrationHashes = new(LoadFrozenReaderMigrationHashes);

    public static MigrationScriptClassification Classify(string scriptName, string sql)
    {
        ArgumentNullException.ThrowIfNull(scriptName);
        ArgumentNullException.ThrowIfNull(sql);

        var breakingRules = IsReviewedImportGuardDefinition(scriptName, sql)
            ? Array.Empty<string>()
            : DetectBreakingRules(sql);

        MigrationSafetyClassification classification;
        if (breakingRules.Count == 0)
        {
            classification = MigrationSafetyClassification.Expand;
        }
        else if (HasCompatibilityReviewMarker(sql) || IsFrozenReaderMigration(scriptName, sql))
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

    private static bool IsFrozenReaderMigration(string scriptName, string sql)
        => FrozenReaderMigrationHashes.Value.TryGetValue(scriptName, out var hash) &&
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql.Replace("\r\n", "\n", StringComparison.Ordinal)))) == hash;

    private static bool IsReviewedImportGuardDefinition(string scriptName, string sql)
    {
        // These exact installations only define import helpers; they do not invoke their
        // DROP/RENAME operations during migration or alter the reader's schema/signatures.
        // Ownership checks and RESTRICT protect importer-owned targets and dependencies.
        // Routine/DO/dynamic-SQL detection remains enabled for every other name or byte
        // change, including appended statements.
        var reviewedHash = scriptName switch
        {
            "129_ProtectImportTableOwnership.sql"
                or "Honua.Server.Migrations.129_ProtectImportTableOwnership.sql"
                or "src/Honua.Server/Migrations/129_ProtectImportTableOwnership.sql"
                => "A62328A559BC22113185838F0EEC3CAC22FA50352F9CD6C9D98BD0E42B9DD77B",
            "145_ProtectImportDependencies.sql"
                or "Honua.Server.Migrations.145_ProtectImportDependencies.sql"
                or "src/Honua.Server/Migrations/145_ProtectImportDependencies.sql"
                => "5137ECDF8A2C23C7503B048BA79EA21CF504B89DED7E0E5A64417724D7DEAEB1",
            _ => null
        };
        return reviewedHash is not null && Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            sql.Replace("\r\n", "\n", StringComparison.Ordinal)))) == reviewedHash;
    }

    private static Dictionary<string, string> LoadFrozenReaderMigrationHashes()
    {
        // Expanding the detector must not require rewriting already-journaled SQL to add
        // comments. The frozen reader baseline is its exact-byte historical review record.
        // These scripts remain CONTRACT (and require the existing upgrade approval); only
        // their missing inline annotation is supplied by the frozen record. The ongoing
        // hash ledger deliberately grants no such exemption to future migrations.
        using var stream = typeof(MigrationSafetyClassifier).Assembly.GetManifestResourceStream("Honua.Core.SchemaReaderBaseline.json")
            ?? throw new InvalidOperationException("The frozen schema reader baseline is missing.");
        using var document = JsonDocument.Parse(stream);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            var name = Path.GetFileName(entry.Name);
            var hash = entry.Value.GetString()!;
            hashes.Add(name, hash);
            hashes.Add((entry.Name.StartsWith("src/Honua.Server/", StringComparison.Ordinal)
                ? "Honua.Server.Migrations."
                : "Honua.Postgres.Migrations.") + name, hash);
        }
        return hashes;
    }

    public static IReadOnlyList<string> DetectBreakingRules(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var scopedSql = ExtractExecutableSql(sql);
        var normalized = scopedSql.Replace(ExecutionScopeBoundary, ' ').Replace(UntrustedDynamicScope, ' ');
        var matchedRules = new List<string>();

        if (DeclaredContractPhaseMarker.IsMatch(sql))
        {
            matchedRules.Add("declared-contract-phase");
        }

        foreach (var (ruleName, pattern) in PotentiallyBreakingPatterns)
        {
            var executable = ruleName == "truncate-table"
                ? string.Join(" ", scopedSql.Split(ExecutionScopeBoundary)
                    .Select(scope => scope.Contains(UntrustedDynamicScope)
                        ? scope.Replace(UntrustedDynamicScope, ' ')
                        : MigrationTriggerEvents.ExcludeDeclarations(scope)))
                : normalized;
            if (pattern.IsMatch(executable))
            {
                matchedRules.Add(ruleName);
            }
        }

        return matchedRules;
    }

    public static bool HasCompatibilityReviewMarker(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return CompatibilityReviewMarker.IsMatch(sql);
    }

    // A single left-to-right lexer pass removes comments and inert literals but recursively retains
    // routine/DO bodies and dynamic SQL executed from them. A regex-per-construct approach is
    // order-sensitive and unsafe: stripping string literals before
    // line comments lets an apostrophe inside a `--` comment (e.g. "-- don't ship" ... "-- isn't used")
    // open a spurious quote span that swallows real DDL between the two comments, hiding a DROP TABLE
    // and misclassifying a contract migration as expand. A lexer honors the real precedence — a `--`
    // or `/* */` outside a string starts a comment, and a `'`/`$tag$` outside a comment starts a
    // literal — so no construct can leak across another's boundary. Executable quoted content is
    // parsed in its own statement scope so concatenated literals join without crossing semicolons.
    // NUL is not valid PostgreSQL source text. Keep execution scopes separate until trigger
    // headers have been removed, so an incomplete dynamic identifier cannot consume SQL
    // from its caller, another statement, or a different literal fragment. Other rules still see all SQL.
    private const char ExecutionScopeBoundary = '\0';
    private const char UntrustedDynamicScope = '\u0001';

    private static string ExtractExecutableSql(string sql)
    {
        var result = new StringBuilder(sql.Length);
        AppendExecutableSql(sql, result, insideRoutineBody: false);
        return result.ToString();
    }

    private static void AppendExecutableSql(string sql, StringBuilder result, bool insideRoutineBody)
        => new ExecutableSqlParser(sql, result, insideRoutineBody).Append();

    private sealed class ExecutableSqlParser(string sql, StringBuilder result, bool insideRoutineBody)
    {
        private int _index;
        private int _statementStart = result.Length;
        private readonly StringBuilder _dynamicSql = new();
        private readonly List<string> _dynamicFragments = new();

        public void Append()
        {
            while (_index < sql.Length)
            {
                if (sql[_index] == '"')
                {
                    _index = AppendQuotedIdentifier(sql, result, _index);
                    continue;
                }

                var commentEnd = MigrationSqlComments.FindEnd(sql, _index);
                if (commentEnd > _index)
                {
                    _index = commentEnd;
                    result.Append(' ');
                    continue;
                }

                if (TryAppendLiteral())
                {
                    continue;
                }

                var current = sql[_index++];
                result.Append(current);
                if (current == ';')
                {
                    CompleteStatement();
                }
            }

            FlushDynamicSql();
        }

        private bool TryAppendLiteral()
        {
            var escapeString = IsEscapeString();
            if (sql[_index] == '\'' || escapeString)
            {
                if (escapeString)
                {
                    _index++;
                }

                var literal = escapeString
                    ? MigrationStringLiterals.ReadEscaped(sql, _index)
                    : MigrationStringLiterals.ReadStandard(sql, _index);
                _index = literal.NextIndex;
                AppendLiteral(literal.Contents);
                return true;
            }

            if (sql[_index] != '$' || TryReadDollarTag(sql, _index) is not { } tag)
            {
                return false;
            }

            var contentsStart = _index + tag.Length;
            var closeIndex = sql.IndexOf(tag, contentsStart, StringComparison.Ordinal);
            var contentsLength = (closeIndex < 0 ? sql.Length : closeIndex) - contentsStart;
            _index = closeIndex < 0 ? sql.Length : closeIndex + tag.Length;
            AppendLiteral(sql.Substring(contentsStart, contentsLength));
            return true;
        }

        private bool IsEscapeString()
            => (sql[_index] is 'E' or 'e') && _index + 1 < sql.Length && sql[_index + 1] == '\''
                && (_index == 0 || !(char.IsAsciiLetterOrDigit(sql[_index - 1])
                    || sql[_index - 1] >= 128 || sql[_index - 1] is '_' or '$'));

        private void AppendLiteral(string contents)
        {
            if (IsRoutineBodyPrefix(result, _statementStart))
            {
                var routineSql = new StringBuilder(contents.Length);
                AppendExecutableSql(contents, routineSql, insideRoutineBody: true);
                AppendExecutionScope(result, routineSql);
            }
            else if (insideRoutineBody
                && IsDynamicExecutePrefix(result, _statementStart)
                && IsDynamicSqlTemplatePosition(result, _statementStart))
            {
                AppendExecutionScope(_dynamicSql, ExtractDynamicSql(contents,
                    result.ToString(_statementStart, result.Length - _statementStart), sql.AsSpan(_index)));
                _dynamicFragments.Add(contents);
            }

            result.Append(' ');
        }

        private void CompleteStatement()
        {
            if (_dynamicSql.Length > 0)
            {
                FlushDynamicSql();
                result.Append(';');
            }

            _statementStart = result.Length;
        }

        private void FlushDynamicSql()
        {
            if (_dynamicSql.Length == 0)
            {
                return;
            }

            AppendExecutionScope(result, _dynamicSql);
            AppendDynamicFragments(result, _dynamicFragments);
            _dynamicSql.Clear();
            _dynamicFragments.Clear();
        }
    }

    private static void AppendDynamicFragments(StringBuilder result, List<string> fragments)
    {
        if (fragments.Count < 2)
        {
            return;
        }

        // Keep adjacent literal fragments contiguous even when their independent lexical
        // views differ. Computed expressions cannot establish a trusted trigger header.
        AppendExecutionScope(result, ExtractUntrustedDynamicSql(string.Concat(fragments)));
        result.Append(ExecutionScopeBoundary);
        result.Append(UntrustedDynamicScope);
        foreach (var fragment in fragments)
        {
            result.Append(MigrationSqlComments.NormalizeFragment(fragment));
        }

        result.Append(ExecutionScopeBoundary);
    }

    private static StringBuilder ExtractDynamicSql(string contents, string prefix, ReadOnlySpan<char> suffix)
    {
        // Only a complete directly executed literal has a known lexical context. Expressions
        // can compute quotes or comment delimiters around a fragment, so parsing that fragment
        // in isolation can discard executable SQL as a string, comment, or trigger identifier.
        suffix = suffix.TrimStart();
        var completeLiteral = DirectDynamicExecutePrefix.IsMatch(prefix)
            && (suffix.IsEmpty || suffix[0] == ';');
        if (!completeLiteral)
        {
            return ExtractUntrustedDynamicSql(contents);
        }

        var literalSql = new StringBuilder(contents.Length);
        AppendExecutableSql(contents, literalSql, insideRoutineBody: false);
        return literalSql;
    }

    private static StringBuilder ExtractUntrustedDynamicSql(string contents)
    {
        var fragmentSql = new StringBuilder(contents.Length);
        fragmentSql.Append(UntrustedDynamicScope);
        fragmentSql.Append(contents);
        // Computed expressions have uncertain quoting. Retain raw, quote-independent,
        // and parsed views so comment markers inside identifiers cannot hide later SQL.
        // No view receives trigger-header exemptions.
        fragmentSql.Append(ExecutionScopeBoundary);
        fragmentSql.Append(UntrustedDynamicScope);
        fragmentSql.Append(MigrationSqlComments.NormalizeFragment(contents));
        fragmentSql.Append(ExecutionScopeBoundary);
        fragmentSql.Append(UntrustedDynamicScope);
        AppendExecutableSql(contents, fragmentSql, insideRoutineBody: false);
        // A preceding delimiter may be identifier text supplied by another expression.
        // Start each candidate independently so that delimiter cannot erase later DDL.
        foreach (Match candidate in BreakingStatementStart.Matches(contents))
        {
            fragmentSql.Append(ExecutionScopeBoundary);
            fragmentSql.Append(UntrustedDynamicScope);
            fragmentSql.Append(MigrationSqlComments.NormalizeStatement(contents, candidate.Index));
        }

        return fragmentSql;
    }

    private static void AppendExecutionScope(StringBuilder result, StringBuilder scope)
    {
        result.Append(ExecutionScopeBoundary);
        result.Append(scope);
        result.Append(ExecutionScopeBoundary);
    }

    private static int AppendQuotedIdentifier(string sql, StringBuilder result, int index)
    {
        result.Append(sql[index++]);
        while (index < sql.Length)
        {
            var current = sql[index++];
            result.Append(current);
            if (current != '"')
            {
                continue;
            }

            if (index < sql.Length && sql[index] == '"')
            {
                result.Append(sql[index++]);
                continue;
            }

            break;
        }

        return index;
    }

    private static bool IsRoutineBodyPrefix(StringBuilder result, int statementStart)
        => RoutineBodyPrefix.IsMatch(result.ToString(statementStart, result.Length - statementStart));

    private static bool IsDynamicExecutePrefix(StringBuilder result, int statementStart)
        => DynamicExecutePrefix.IsMatch(result.ToString(statementStart, result.Length - statementStart));

    private static bool IsDynamicSqlTemplatePosition(StringBuilder result, int statementStart)
        => !DynamicFormatArgumentPrefix.IsMatch(result.ToString(statementStart, result.Length - statementStart));

    private static readonly Regex RoutineBodyPrefix = new(
        @"(?:\bCREATE\s+(?:OR\s+REPLACE\s+)?(?:FUNCTION|PROCEDURE)\b[^;]*\bAS\s*|\bDO(?:\s+LANGUAGE\s+[A-Za-z_][A-Za-z0-9_$]*)?\s*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex BreakingStatementStart = new(
        @"\b(?:ALTER|DROP|TRUNCATE)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DirectDynamicExecutePrefix = new(
        @"\bEXECUTE\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DynamicExecutePrefix = new(
        @"\bEXECUTE\b(?!\s+FUNCTION\b)[^;]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DynamicFormatArgumentPrefix = new(
        @"\bEXECUTE\b(?!\s+FUNCTION\b)[^;]*\bFORMAT\s*\([^;]*,[^;]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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

        if (i >= length || sql[i] != '$')
        {
            return null;
        }

        var tag = sql[start..(i + 1)];
        // DbUp variables use the same delimiters as PostgreSQL dollar quotes. They are
        // substituted before execution and must remain visible to the migration-safety
        // classifier; treating $HonuaSchema$ as a quoted body can hide following DDL.
        return string.Equals(tag, "$HonuaSchema$", StringComparison.OrdinalIgnoreCase)
            ? null
            : tag;
    }

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
