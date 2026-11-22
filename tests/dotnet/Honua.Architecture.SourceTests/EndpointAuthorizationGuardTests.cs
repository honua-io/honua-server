// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

[Trait("Category", "Architecture")]
public sealed class EndpointAuthorizationGuardTests
{
    private static readonly string[] FeaturesRelativePaths = ["src/Honua.Server/Features", "src/Honua.Ai/Features"];
    private const string ProgramRelativePath = "src/Honua.Server/Program.cs";
    private const string BaselineRelativePath = "tests/dotnet/Honua.Architecture.SourceTests/endpoint-authorization-baseline.txt";
    private static readonly string[] MutationVerbs = ["MapPost", "MapPut", "MapDelete", "MapPatch"];
    private static readonly string[] BaselinedVerbs = ["MapGet", "MapMethods"];

    // Mutation routes that are reachable without credentials on purpose and authorize inside the
    // handler. The MCP POST dispatcher admits only the anonymous handshake, discovery and listing,
    // and rejects anonymous tools/call and resources/read; the MCP DELETE checks that the session
    // belongs to the caller. Add an entry only with that kind of in-handler check.
    private static readonly string[] ReviewedAnonymousMutationRoutes =
    [
        "src/Honua.Ai/Features/Protocols/Mcp/Mcp/McpEndpointExtensions.cs MapDelete RoutePath",
        "src/Honua.Ai/Features/Protocols/Mcp/Mcp/McpEndpointExtensions.cs MapPost RoutePath",
    ];

    private static readonly string[] AuthOptInMarkers =
    {
        "RequireAuthorization",
        "RequireAdminAuthorization",
        // Narrow operation-approval policy: admits only full administrators
        // or scoped admin API keys carrying the explicit admin:approve grant.
        "RequireAdminApproveAuthorization",
        "RequireAdminApiKey",
        "RequirePermission",
        "RequireRole",
        "RequireScope",
        "AllowAnonymous",
        // Temporal history auth helpers wrap RequireAuthorization with a distinct
        // policy per surface (history read, diff read, rollback execute); recognize them explicitly.
        "RequireTemporalHistoryRead",
        "RequireTemporalDiffRead",
        "RequireTemporalRollbackExecute",
        // Studio lifecycle policy: method-scoped admin grants plus flag-gated end-user ownership
        // checks performed by StudioEndpointAuthorization in each handler.
        "RequireStudioLifecycleAuthorization",
        // Studio AI proxy policy: composes the lifecycle flag gate with the stricter interactive
        // principal boundary used before any model-provider call.
        "RequireStudioAiProxyAuthorization",
        // Read-only ops-reader policy (A12): a method-aware Require*Authorization helper that admits
        // ops:read/admin for safe reads while still requiring full admin write for mutating routes.
        "RequireOpsReadAuthorization",
        // Controlled-conformance mutation policy: a narrowly scoped write
        // credential for the scheduled SDK conformance runner, additionally gated per run by an
        // ownership token checked inside each handler.
        "RequireConformanceMutateAuthorization",
    };

    [ArchitectureTest]
    public void EveryMutationEndpoint_HasExplicitAuthorizationDecision()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();

        var found = FindUnmarkedRoutes(repoRoot, MutationVerbs);
        var gaps = SubtractMultiset(found, ReviewedAnonymousMutationRoutes);
        var stale = SubtractMultiset(ReviewedAnonymousMutationRoutes, found);

        stale.Should().BeEmpty("reviewed anonymous mutation routes must still exist unmarked: " + string.Join("; ", stale));
        gaps.Should().BeEmpty(
            "every HTTP mutation route in any src project must call one of "
            + string.Join(", ", AuthOptInMarkers)
            + " on the route itself, its parent group or the group passed into its mapping method; intentionally "
            + "public endpoints must opt in via AllowAnonymous() so the decision is visible to reviewers. Unmarked: "
            + string.Join("; ", gaps));
    }

    [ArchitectureTest]
    public void EveryReadEndpoint_HasExplicitAuthorizationDecisionOrReviewedBaselineEntry()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var gaps = FindUnmarkedRoutes(repoRoot, BaselinedVerbs);
        var baseline = ReadBaseline(repoRoot);

        var unreviewed = SubtractMultiset(gaps, baseline);
        var stale = SubtractMultiset(baseline, gaps);

        unreviewed.Should().BeEmpty(
            "there is no fallback authorization policy, so a GET or MapMethods route without an authorization "
            + "marker is anonymous unless its handler checks access; mark the route or group, or add the route to "
            + BaselineRelativePath + " after confirming the handler's access check. Unreviewed: "
            + string.Join("; ", unreviewed));
        stale.Should().BeEmpty(
            "baseline entries must match an unmarked route; remove entries for routes that were marked, renamed "
            + "or deleted from " + BaselineRelativePath + ". Stale: " + string.Join("; ", stale));
    }

    [ArchitectureTest]
    public void GrpcSpecServiceMapping_RequiresCanonicalAdminAuthorization()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var programPath = ArchitectureTestHelpers.CombinePath(repoRoot, ProgramRelativePath);
        var program = File.ReadAllText(programPath);

        var authorizedMapping = new Regex(
            @"MapGrpcService<Honua\.Server\.Features\.Spec\.HonuaSpecService>\s*\(\s*\)\s*\.RequireAdminAuthorization\s*\(\s*\)",
            RegexOptions.CultureInvariant);

        authorizedMapping.IsMatch(program).Should().BeTrue(
            "geospatial.v1.SpecService plan/apply/cancel operations must inherit the same "
            + "Admin authorization policy as their POST-based REST twins before handler execution");
    }

    [ArchitectureTest]
    public void AuditedFeaturesDirectory_ContainsEndpointFiles()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var featureRoots = FeaturesRelativePaths.Select(path => ArchitectureTestHelpers.CombinePath(repoRoot, path)).ToArray();
        foreach (var featuresRoot in featureRoots)
        {
            Directory.EnumerateFiles(featuresRoot, "*Endpoints.cs", SearchOption.AllDirectories).Should().NotBeEmpty();
        }

        var fileCount = featureRoots.Sum(path => Directory.EnumerateFiles(path, "*Endpoints.cs", SearchOption.AllDirectories).Count());

        fileCount.Should().BeGreaterThan(50,
            "the architecture guard depends on scanning *Endpoints.cs files in src/Honua.Server/Features; "
            + "a count this low usually means the directory layout changed and the regex needs to follow.");
    }

    // Helpers that take a route builder and apply AllowAnonymous or an authorization policy to it,
    // trusted only inside the directory that defines them.
    private const string SensorThingsDirectory = "src/Honua.Protocols/SensorThings/";
    private static readonly string[] SensorThingsAuthorizationWrappers =
    {
        "ConfigureCoreWrite",
        "ConfigureWriteAuthorization",
    };

    private static readonly Regex MapRouteRegex = new(
        @"\.(?<verb>MapGet|MapPost|MapPut|MapDelete|MapPatch|MapMethods)\s*\(\s*(?<route>[^,]+?)\s*,",
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
            var scanned = new ScannedFile(relativePath, content, CollectGroupAuthFlags(content));
            gaps.AddRange(FindUnmarkedRoutesInFile(scanned, verbs).Select(route => relativePath + " " + route));
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

    private static IEnumerable<string> FindUnmarkedRoutesInFile(ScannedFile file, IReadOnlyCollection<string> verbs)
    {
        foreach (Match match in MapRouteRegex.Matches(file.Content))
        {
            var verb = match.Groups["verb"].Value;
            if (!verbs.Contains(verb) || HasAuthorizationDecision(file, match.Index))
            {
                continue;
            }

            // Route expressions (literals, constants, interpolated strings) are kept as written,
            // with whitespace collapsed so each baseline entry stays on one line.
            var route = Regex.Replace(match.Groups["route"].Value, @"\s+", " ");
            yield return verb + " " + route;
        }
    }

    private static bool HasAuthorizationDecision(ScannedFile file, int matchIndex)
    {
        var content = file.Content;
        var chain = ExtractFluentChain(content, matchIndex);
        if (AuthOptInMarkers.Any(chain.Contains) || IsInsideAuthorizationWrapper(file, matchIndex))
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

    private static bool IsInsideAuthorizationWrapper(ScannedFile file, int matchIndex)
    {
        if (!file.RelativePath.StartsWith(SensorThingsDirectory, System.StringComparison.Ordinal))
        {
            return false;
        }

        var statementPrefix = file.Content[FindStatementStart(file.Content, matchIndex)..matchIndex];
        return SensorThingsAuthorizationWrappers.Any(statementPrefix.Contains);
    }

    // A route mapped on a builder parameter of a private helper, e.g.
    // `private static void MapImportRoutes(IEndpointRouteBuilder group, ...)`, inherits the
    // group's decision when every use of that helper in the file passes an authorized MapGroup
    // variable as its first argument. Calls from other partial-class files are not seen, so the
    // nearest enclosing declaration must be the private helper itself.
    private static bool IsAuthorizedGroupParameter(ScannedFile file, int matchIndex, string receiver)
    {
        var declarations = Regex.Matches(
            file.Content[..matchIndex],
            @"(?<name>\w+)\s*\(\s*(?:this\s+)?(?:IEndpointRouteBuilder|RouteGroupBuilder)\s+" + Regex.Escape(receiver) + @"\b",
            RegexOptions.CultureInvariant);
        if (declarations.Count == 0 || !IsDeclaration(file.Content, declarations[^1].Index))
        {
            return false;
        }

        var name = declarations[^1].Groups["name"].Value;
        var uses = Regex.Matches(file.Content, @"\b" + Regex.Escape(name) + @"\s*\(", RegexOptions.CultureInvariant)
            .Where(use => !IsDeclaration(file.Content, use.Index))
            .ToList();
        return uses.Count > 0 && uses.All(use => PassesAuthorizedGroup(file, use.Index + use.Length));
    }

    private static bool IsDeclaration(string content, int nameIndex)
        => Regex.IsMatch(content[FindStatementStart(content, nameIndex)..nameIndex], @"\bprivate\s+static\s+[\w<>?]+\s+$");

    private static bool PassesAuthorizedGroup(ScannedFile file, int argumentsStart)
    {
        var argument = Regex.Match(file.Content[argumentsStart..], @"^\s*(?<arg>\w+)\s*[,)]", RegexOptions.CultureInvariant);
        return argument.Success && file.GroupAuth.TryGetValue(argument.Groups["arg"].Value, out var authorized) && authorized;
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
    private sealed record ScannedFile(string RelativePath, string Content, Dictionary<string, bool> GroupAuth);
}
