// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Honua.Architecture.Tests;

public sealed partial class EndpointAuthorizationGuardTests
{
    // Helpers that take a route builder and apply AllowAnonymous or an authorization policy to it.
    private static readonly string[] AuthorizationWrapperMarkers =
    {
        "ConfigureCoreWrite",
        "ConfigureWriteAuthorization",
    };

    private static readonly Regex MapRouteRegex = new(
        @"\.(?<verb>MapGet|MapPost|MapPut|MapDelete|MapPatch|MapMethods)\s*\(\s*(?:""(?<route>[^""]*)""|@""(?<route2>[^""]*)"")",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MapGroupRegex = new(
        @"\bMapGroup\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly char[] StatementBoundaryChars = { ';', '{', '}' };

    private static List<string> FindUnmarkedRoutes(string repoRoot, IReadOnlyCollection<string> verbs)
    {
        var gaps = new List<string>();
        foreach (var file in EnumerateSourceFiles(repoRoot))
        {
            var content = StripComments(File.ReadAllText(file));
            var relativePath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            gaps.AddRange(FindUnmarkedRoutesInFile(content, verbs).Select(route => relativePath + " " + route));
        }

        gaps.Sort(System.StringComparer.Ordinal);
        return gaps;
    }

    private static IEnumerable<string> EnumerateSourceFiles(string repoRoot)
    {
        var sourceRoot = ArchitectureTestHelpers.CombinePath(repoRoot, "src");
        return Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(Path.GetRelativePath(sourceRoot, path)));
    }

    private static bool IsBuildOutput(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Contains("bin") || segments.Contains("obj");
    }

    private static IEnumerable<string> FindUnmarkedRoutesInFile(string content, IReadOnlyCollection<string> verbs)
    {
        var file = new ScannedFile(content, CollectGroupAuthFlags(content));
        foreach (Match match in MapRouteRegex.Matches(content))
        {
            var verb = match.Groups["verb"].Value;
            if (!verbs.Contains(verb) || HasAuthorizationDecision(file, match.Index))
            {
                continue;
            }

            var route = match.Groups["route"].Success ? match.Groups["route"].Value : match.Groups["route2"].Value;
            yield return verb + " " + route;
        }
    }

    private static bool HasAuthorizationDecision(ScannedFile file, int matchIndex)
    {
        var content = file.Content;
        var chain = ExtractFluentChain(content, matchIndex);
        if (AuthOptInMarkers.Any(chain.Contains) || IsInsideAuthorizationWrapper(content, matchIndex))
        {
            return true;
        }

        var receiver = ExtractReceiverBefore(content, matchIndex);
        if (receiver is not null && IsAuthorizedReceiver(file, matchIndex, receiver))
        {
            return true;
        }

        var assignedVar = ExtractAssignedVarBefore(content, matchIndex);
        return assignedVar is not null && HasPostStatementAuthCall(content, matchIndex + chain.Length, assignedVar);
    }

    private static bool IsAuthorizedReceiver(ScannedFile file, int matchIndex, string receiver)
        => file.GroupAuth.TryGetValue(receiver, out var hasGroupAuth)
            ? hasGroupAuth
            : IsAuthorizedGroupParameter(file, matchIndex, receiver);

    private static bool IsInsideAuthorizationWrapper(string content, int matchIndex)
    {
        var statementPrefix = content[FindStatementStart(content, matchIndex)..matchIndex];
        return AuthorizationWrapperMarkers.Any(statementPrefix.Contains);
    }

    // A route mapped on a builder parameter, e.g. `MapImportRoutes(IEndpointRouteBuilder group)`,
    // inherits the group's decision when every call to that method in the file passes an
    // authorized MapGroup variable.
    private static bool IsAuthorizedGroupParameter(ScannedFile file, int matchIndex, string receiver)
    {
        var declarations = Regex.Matches(
            file.Content[..matchIndex],
            @"(?<name>\w+)\s*\(\s*(?:this\s+)?(?:IEndpointRouteBuilder|RouteGroupBuilder)\s+" + Regex.Escape(receiver) + @"\b",
            RegexOptions.CultureInvariant);
        if (declarations.Count == 0)
        {
            return false;
        }

        var declaration = declarations[^1];
        var calls = Regex.Matches(
                file.Content,
                @"\b" + Regex.Escape(declaration.Groups["name"].Value) + @"\s*\(\s*(?<arg>\w+)\s*[,)]",
                RegexOptions.CultureInvariant)
            .Where(call => call.Index != declaration.Index)
            .ToList();
        return calls.Count > 0 && calls.All(call => file.GroupAuth.TryGetValue(call.Groups["arg"].Value, out var authorized) && authorized);
    }

    private static List<string> ReadBaseline(string repoRoot)
        => File.ReadAllLines(ArchitectureTestHelpers.CombinePath(repoRoot, BaselineRelativePath))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();

    private static List<string> SubtractMultiset(IEnumerable<string> source, IEnumerable<string> toRemove)
    {
        var remaining = source.ToList();
        foreach (var entry in toRemove)
        {
            remaining.Remove(entry);
        }

        return remaining;
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

            groups[assignment.Groups["var"].Value] = AuthOptInMarkers.Any(marker => statement.Contains(marker));
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
        lineStart = lineStart < 0 ? 0 : lineStart + 1;

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
        var end = FindStatementEnd(content, matchIndex);
        return content[matchIndex..end];
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
            depth += content[i] switch
            {
                '(' => 1,
                ')' => -1,
                _ => 0,
            };
            if (content[i] == ';' && depth <= 0)
            {
                return i;
            }
        }

        return content.Length;
    }

    // One source file with its comments stripped and its MapGroup variables mapped to whether
    // the group carries an authorization decision.
    private sealed record ScannedFile(string Content, Dictionary<string, bool> GroupAuth);
}
