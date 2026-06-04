// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Fast, container-free guard for the DbUp migration runner's variable
/// substitution. The runner (<c>PostgresDatabaseMigrationRunner</c>) executes the
/// embedded <c>*.sql</c> migrations through DbUp with variable substitution
/// ENABLED, which treats a dollar-delimited token that has an inner word — a
/// NAMED dollar-quote tag such as <c>$ddl$</c> or <c>$func$</c> — as a variable
/// reference and fails the whole migration with
/// "Variable &lt;name&gt; has no value defined" BEFORE the SQL ever runs. The
/// substitution scans the entire script text, including comments.
/// </summary>
/// <remarks>
/// This class of bug is invisible to a local <c>psql</c> check (psql does not do
/// DbUp substitution) and to the integration-test hosts (which skip migrations),
/// so it can only surface once the server actually boots with migrations against
/// a real database. This static convention check catches it instantly — no
/// container, no database — and runs in the architecture-tests job (which is not
/// subject to the server-test matrix's fail-fast cancellation).
///
/// The fix when this fails: use ONLY the anonymous <c>$$</c> dollar-quote (which
/// DbUp ignores, as it has no inner word) for any plpgsql <c>DO</c>/function body,
/// and write the inner DDL as direct statements or single-quoted <c>EXECUTE</c>
/// strings. See <c>043_CreatePgRoutingTopology.sql</c> for the canonical pattern.
/// </remarks>
[Trait("Category", "Architecture")]
public sealed class MigrationScriptDbUpSafetyTests
{
    /// <summary>
    /// Migration directories whose <c>*.sql</c> scripts are executed through DbUp
    /// (with variable substitution enabled). Rooted at the repository root.
    /// </summary>
    private static readonly string[] _migrationDirectories =
    {
        Path.Combine("src", "Honua.Server", "Migrations"),
        Path.Combine("src", "Honua.Postgres", "Migrations"),
    };

    // A named dollar-quote tag: a single '$' (not preceded by another '$', so the
    // anonymous "$$ ... $$" delimiter is excluded) followed by an identifier and a
    // closing '$'. This is exactly the shape DbUp's variable substitution
    // misinterprets as "$variable$".
    private static readonly Regex _namedDollarQuoteTag = new(
        @"(?<!\$)\$[A-Za-z_][A-Za-z0-9_]*\$",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(2));

    [ArchitectureTest]
    public void MigrationScripts_ShouldNotUseNamedDollarQuoteTags_ForDbUpCompatibility()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();

        var scannedCount = 0;
        var violations = new List<string>();

        foreach (var relativeDirectory in _migrationDirectories)
        {
            var directory = Path.Combine(repositoryRoot, relativeDirectory);
            Directory.Exists(directory).Should().BeTrue(
                $"migration directory '{relativeDirectory}' must exist; update the scan list if it was moved.");

            foreach (var file in Directory.EnumerateFiles(directory, "*.sql", SearchOption.AllDirectories))
            {
                scannedCount++;
                var sql = File.ReadAllText(file);
                var matches = _namedDollarQuoteTag.Matches(sql);
                if (matches.Count == 0)
                {
                    continue;
                }

                var tags = matches
                    .Select(m => m.Value)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(t => t, StringComparer.Ordinal);

                var relativeFile = Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/');
                violations.Add($"{relativeFile}: {string.Join(", ", tags)}");
            }
        }

        scannedCount.Should().BeGreaterThan(0,
            "the migration scan must find scripts; an empty scan means the directory layout changed and the guard is silently disabled.");

        violations.Should().BeEmpty(
            "DbUp's variable substitution treats named dollar-quote tags as variables and fails the migration with "
            + "\"Variable <name> has no value defined\" before the SQL runs (it scans comments too). Use only the "
            + "anonymous \"$$\" dollar-quote with direct plpgsql statements / single-quoted EXECUTE, as in "
            + "043_CreatePgRoutingTopology.sql. Offending scripts:\n  "
            + string.Join("\n  ", violations));
    }

    [ArchitectureTest]
    public void DetectionRegex_MatchesNamedTags_AndIgnoresAnonymousDollarQuotes()
    {
        // Named dollar-quote tags (the DbUp-incompatible shape) must be detected.
        _namedDollarQuoteTag.IsMatch("EXECUTE $ddl$ CREATE TABLE t() $ddl$;").Should().BeTrue();
        _namedDollarQuoteTag.IsMatch("EXECUTE $cmt$COMMENT ON TABLE t IS 'x'$cmt$;").Should().BeTrue();
        _namedDollarQuoteTag.IsMatch("DO $body$ BEGIN END $body$;").Should().BeTrue();
        // The comment-embedded literal that re-triggered the original bug.
        _namedDollarQuoteTag.IsMatch("-- do not use $ddl$ tags").Should().BeTrue();

        // The anonymous "$$ ... $$" block (DbUp-safe) must NOT be flagged, including
        // a single-word body with no surrounding whitespace.
        _namedDollarQuoteTag.IsMatch("DO $$ BEGIN PERFORM 1; END $$;").Should().BeFalse();
        _namedDollarQuoteTag.IsMatch("DO $$\nBEGIN\nEND\n$$;").Should().BeFalse();
        _namedDollarQuoteTag.IsMatch("SELECT $$word$$;").Should().BeFalse();
    }
}
