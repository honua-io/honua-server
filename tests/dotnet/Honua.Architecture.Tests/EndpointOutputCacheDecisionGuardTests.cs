// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Regression guard for SEC-20. The shared output cache keys on the request URL
/// plus the base policy's vary-by values (tenant, license), so a read route whose
/// response depends on a credential presented in a request header or query
/// parameter — or that returns a credential — is not a function of its cache key.
/// Every read route declared in an endpoint file that handles one of those
/// credential families must therefore state an output-cache decision explicitly
/// (<c>CacheOutput(...)</c> on the route or on its parent group), rather than
/// inheriting whatever the shared base policy happens to do.
/// </summary>
[Trait("Category", "Architecture")]
public sealed class EndpointOutputCacheDecisionGuardTests
{
    private const string SourceRelativePath = "src";

    /// <summary>
    /// Literals that mark an endpoint file as handling an application-defined
    /// credential: the credential-bearing request headers, and the metadata marker
    /// applied to routes whose response body is itself a credential.
    /// </summary>
    private static readonly string[] CredentialSurfaceMarkers =
    {
        "X-API-Key",
        "X-Esri-Authorization",
        "X-Honua-Embed-Key",
        "X-Honua-Token",
        "CredentialResponseCacheMetadata",
    };

    private const string CacheDecisionMarker = "CacheOutput";

    private static readonly Regex MapReadRegex = new(
        @"\.(MapGet|MapMethods)\s*\(\s*(?:""(?<route>[^""]*)""|@""(?<route2>[^""]*)""|(?<route3>[\w\.]+))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MapGroupRegex = new(
        @"\bMapGroup\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly char[] StatementBoundaryChars = { ';', '{', '}' };

    /// <summary>
    /// Every read route mapped in an endpoint file that handles a credential
    /// header, a credential query parameter, or a credential response body must
    /// declare its own output-cache decision. Routes that are genuinely public and
    /// URL-pure opt into a named policy; the rest opt out with
    /// <c>CacheOutput(policy =&gt; policy.NoCache())</c>. Silence is the gap this
    /// guard closes.
    /// </summary>
    [ArchitectureTest]
    public void EveryReadEndpointOnACredentialSurface_DeclaresACacheDecision()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var sourceRoot = ArchitectureTestHelpers.CombinePath(repoRoot, SourceRelativePath);
        Directory.Exists(sourceRoot)
            .Should().BeTrue($"the audited source directory should exist at {sourceRoot}");

        var gaps = new List<string>();
        var scanned = 0;

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*Endpoints.cs", SearchOption.AllDirectories))
        {
            var raw = File.ReadAllText(file);
            var content = StripComments(raw);

            if (!CredentialSurfaceMarkers.Any(marker => content.Contains(marker, StringComparison.Ordinal)))
            {
                continue;
            }

            scanned++;
            var groupsWithCacheDecision = CollectGroupCacheDecisions(content);

            foreach (Match match in MapReadRegex.Matches(content))
            {
                var route = match.Groups["route"].Success ? match.Groups["route"].Value
                    : match.Groups["route2"].Success ? match.Groups["route2"].Value
                    : match.Groups["route3"].Value;

                var chain = ExtractFluentChain(content, match.Index);
                if (chain.Contains(CacheDecisionMarker, StringComparison.Ordinal))
                {
                    continue;
                }

                var receiver = ExtractReceiverBefore(content, match.Index);
                if (receiver is not null
                    && groupsWithCacheDecision.TryGetValue(receiver, out var groupHasDecision)
                    && groupHasDecision)
                {
                    continue;
                }

                var lineNumber = raw[..match.Index].Count(c => c == '\n') + 1;
                gaps.Add($"{Path.GetRelativePath(repoRoot, file)}:{lineNumber} {match.Groups[1].Value} {route}");
            }
        }

        scanned.Should().BeGreaterThan(
            0,
            "the guard depends on finding endpoint files that handle "
            + string.Join(", ", CredentialSurfaceMarkers)
            + "; zero matches usually means the header constants moved and this list needs to follow.");

        gaps.Should().BeEmpty(
            "a read route on a credential-handling endpoint file must call CacheOutput(...) on itself "
            + "or on its parent group. Responses that vary by a credential the caller presents, or that "
            + "carry a credential, are not a function of the URL the shared output cache keys on, so the "
            + "decision has to be stated rather than inherited from the base policy.");
    }

    private static string StripComments(string source)
    {
        var noBlockComments = Regex.Replace(source, "/\\*.*?\\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(noBlockComments, "//[^\n]*", string.Empty);
    }

    private static Dictionary<string, bool> CollectGroupCacheDecisions(string content)
    {
        var groups = new Dictionary<string, bool>(StringComparer.Ordinal);

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

            groups[assignment.Groups["var"].Value] =
                statement.Contains(CacheDecisionMarker, StringComparison.Ordinal);
        }

        return groups;
    }

    private static string? ExtractReceiverBefore(string content, int matchIndex)
    {
        var slice = content[..matchIndex];
        var trailing = Regex.Match(slice, @"(?<name>\w+)\s*$", RegexOptions.CultureInvariant);
        return trailing.Success ? trailing.Groups["name"].Value : null;
    }

    private static string ExtractFluentChain(string content, int matchIndex)
    {
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

    private static int FindStatementStart(string content, int index)
    {
        var i = index;
        while (i > 0 && !StatementBoundaryChars.Contains(content[i - 1]))
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
