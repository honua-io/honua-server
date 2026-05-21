// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Regression guard for the audit finding (#1144): every HTTP mutation
/// endpoint declared via the minimal-API <c>MapPost/MapPut/MapDelete/MapPatch</c>
/// surface under <c>src/Honua.Server/Features</c> must opt into an explicit
/// authorization decision — either a <c>Require*Authorization</c>/<c>RequireAdminApiKey</c>
/// helper on the route or its parent group, or an explicit
/// <c>AllowAnonymous()</c> when the surface is intentionally public (read-only
/// OGC/REST POST mirrors, signed-token issuers, etc.).
/// </summary>
[Trait("Category", "Architecture")]
public sealed class EndpointAuthorizationGuardTests
{
    private const string FeaturesRelativePath = "src/Honua.Server/Features";

    private static readonly string[] AuthOptInMarkers =
    {
        "RequireAuthorization",
        "RequireAdminAuthorization",
        "RequireAdminApiKey",
        "RequirePermission",
        "RequireRole",
        "RequireScope",
        "AllowAnonymous",
    };

    private static readonly Regex MapMutationRegex = new(
        @"\.(MapPost|MapPut|MapDelete|MapPatch)\s*\(\s*(?:""(?<route>[^""]*)""|@""(?<route2>[^""]*)"")",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MapGroupRegex = new(
        @"\bMapGroup\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly char[] StatementBoundaryChars = { ';', '{', '}' };

    /// <summary>
    /// Every mutation route — direct or inherited through a parent group — must
    /// declare its authorization intent explicitly. Routes that should remain
    /// public must opt in via <c>AllowAnonymous()</c> so reviewers can audit
    /// the decision; routes that protect data must reference one of the
    /// <c>Require*</c> helpers. Anything else is treated as an accidental gap.
    /// </summary>
    [ArchitectureTest]
    public void EveryMutationEndpoint_HasExplicitAuthorizationDecision()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var featuresRoot = Path.Combine(repoRoot, FeaturesRelativePath);
        Directory.Exists(featuresRoot)
            .Should().BeTrue($"the audited features directory should exist at {featuresRoot}");

        var gaps = new List<string>();

        foreach (var file in Directory.EnumerateFiles(featuresRoot, "*Endpoints.cs", SearchOption.AllDirectories))
        {
            var raw = File.ReadAllText(file);
            var content = StripComments(raw);
            var groupAuth = CollectGroupAuthFlags(content);

            foreach (Match match in MapMutationRegex.Matches(content))
            {
                var route = match.Groups["route"].Success
                    ? match.Groups["route"].Value
                    : match.Groups["route2"].Value;

                var receiver = ExtractReceiverBefore(content, match.Index);
                var assignedVar = ExtractAssignedVarBefore(content, match.Index);
                var chain = ExtractFluentChain(content, match.Index);

                var hasLocal = AuthOptInMarkers.Any(marker => chain.Contains(marker));
                var hasGroup = receiver is not null
                    && groupAuth.TryGetValue(receiver, out var flags)
                    && flags;
                var hasPostStatement = assignedVar is not null
                    && HasPostStatementAuthCall(content, match.Index + chain.Length, assignedVar);

                if (hasLocal || hasGroup || hasPostStatement)
                {
                    continue;
                }

                var lineNumber = raw[..match.Index].Count(c => c == '\n') + 1;
                gaps.Add($"{Path.GetRelativePath(repoRoot, file)}:{lineNumber} {match.Groups[1].Value} {route}");
            }
        }

        gaps.Should().BeEmpty(
            "every HTTP mutation route under src/Honua.Server/Features must call one of "
            + string.Join(", ", AuthOptInMarkers)
            + " on either the route itself or its parent group; intentionally public endpoints "
            + "must opt in via AllowAnonymous() so the decision is visible to reviewers.");
    }

    /// <summary>
    /// Sanity check: the audited feature surface is non-empty. Guards against a
    /// regression where the directory moves and the scan silently passes
    /// because no files are discovered.
    /// </summary>
    [ArchitectureTest]
    public void AuditedFeaturesDirectory_ContainsEndpointFiles()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var featuresRoot = Path.Combine(repoRoot, FeaturesRelativePath);

        var fileCount = Directory.EnumerateFiles(featuresRoot, "*Endpoints.cs", SearchOption.AllDirectories).Count();

        fileCount.Should().BeGreaterThan(50,
            "the architecture guard depends on scanning *Endpoints.cs files in src/Honua.Server/Features; "
            + "a count this low usually means the directory layout changed and the regex needs to follow.");
    }

    private static string StripComments(string source)
    {
        // Strip C# line and block comments so accidental matches inside
        // documentation comments do not skew the scan.
        var noBlockComments = Regex.Replace(source, "/\\*.*?\\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(noBlockComments, "//[^\n]*", string.Empty);
    }

    private static Dictionary<string, bool> CollectGroupAuthFlags(string content)
    {
        // Map "<variable assigned to a MapGroup(...) call>" => "does the group's
        // fluent chain include any auth-opt-in marker?".
        var groups = new Dictionary<string, bool>(System.StringComparer.Ordinal);

        foreach (Match match in MapGroupRegex.Matches(content))
        {
            var statementStart = FindStatementStart(content, match.Index);
            var statementEnd = FindStatementEnd(content, match.Index);
            if (statementEnd <= statementStart)
            {
                continue;
            }

            var statement = content[statementStart..statementEnd];
            var assignment = Regex.Match(
                statement,
                @"(?:var\s+)?(?<var>\w+)\s*=\s*[\w\.]*\s*\.?\s*MapGroup\s*\(",
                RegexOptions.CultureInvariant);
            if (!assignment.Success)
            {
                continue;
            }

            var variableName = assignment.Groups["var"].Value;
            var hasAuth = AuthOptInMarkers.Any(marker => statement.Contains(marker));
            groups[variableName] = hasAuth;
        }

        return groups;
    }

    private static string? ExtractReceiverBefore(string content, int matchIndex)
    {
        // The receiver of `.MapPost("/route", ...)` is the identifier
        // immediately to the left of the dot. That identifier is what binds
        // the route to a route group (or `endpoints` for the root builder).
        var slice = content[..matchIndex];
        var trailing = Regex.Match(slice, @"(?<name>\w+)\s*$", RegexOptions.CultureInvariant);
        return trailing.Success ? trailing.Groups["name"].Value : null;
    }

    private static string? ExtractAssignedVarBefore(string content, int matchIndex)
    {
        // Recognise `var X = receiver.MapPost(...)` so we can pick up
        // `X.AllowAnonymous();` that follows the statement.
        var lineStart = content.LastIndexOfAny(StatementBoundaryChars, matchIndex - 1);
        if (lineStart < 0)
        {
            lineStart = 0;
        }
        else
        {
            lineStart++;
        }

        var prefix = content[lineStart..matchIndex];
        var assignment = Regex.Match(
            prefix.Replace('\n', ' '),
            @"(?:var\s+)?(?<var>\w+)\s*=\s*[\w\.]*\s*$",
            RegexOptions.CultureInvariant);
        return assignment.Success ? assignment.Groups["var"].Value : null;
    }

    private static string ExtractFluentChain(string content, int matchIndex)
    {
        // Walk forward from the MapPost/MapPut/etc. call until the statement
        // terminator at parenthesis depth zero. That delimits the full
        // fluent-builder chain attached to this route.
        var depth = 0;
        for (var i = matchIndex; i < content.Length; i++)
        {
            var ch = content[i];
            if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')')
            {
                depth--;
            }
            else if (ch == ';' && depth <= 0)
            {
                return content[matchIndex..i];
            }
        }

        return content[matchIndex..];
    }

    private static bool HasPostStatementAuthCall(string content, int chainEnd, string assignedVariable)
    {
        // Catch the pattern used in some feature files where the route is
        // captured into a variable and a follow-up statement attaches
        // authorization, e.g. `var route = endpoints.MapPost(...); route.AllowAnonymous();`.
        var tail = content[chainEnd..];
        var pattern = new Regex(
            @"\b" + Regex.Escape(assignedVariable) + @"\s*\.\s*("
                + string.Join("|", AuthOptInMarkers.Select(Regex.Escape))
                + @")\b",
            RegexOptions.CultureInvariant);
        return pattern.IsMatch(tail);
    }

    private static int FindStatementStart(string content, int index)
    {
        var i = index;
        while (i > 0 && content[i - 1] != ';' && content[i - 1] != '{' && content[i - 1] != '}')
        {
            i--;
        }
        return i;
    }

    private static int FindStatementEnd(string content, int index)
    {
        var depth = 0;
        for (var i = index; i < content.Length; i++)
        {
            var ch = content[i];
            if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')')
            {
                depth--;
            }
            else if (ch == ';' && depth <= 0)
            {
                return i;
            }
        }
        return content.Length;
    }
}
