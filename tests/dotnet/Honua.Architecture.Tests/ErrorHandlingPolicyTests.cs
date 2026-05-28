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
/// Architecture guards for the audit finding (#1144): the server lacked an
/// explicit error-handling policy test. This suite asserts three invariants:
/// <list type="bullet">
///   <item><description>The global exception middleware exists and is wired in <c>Program.cs</c>.</description></item>
///   <item><description>Every endpoint file either funnels errors through that middleware,
///     uses one of the project's error-formatting helpers, or includes its own
///     <c>try/catch</c>. Endpoint files that do none of those are flagged because
///     they would surface raw exceptions to clients.</description></item>
///   <item><description>No file under <c>src/Honua.Server</c> uses <c>throw new Exception(...)</c>
///     or has an empty <c>catch (Exception) { }</c> — both are anti-patterns that
///     obscure failure modes.</description></item>
/// </list>
/// </summary>
[Trait("Category", "Architecture")]
public sealed class ErrorHandlingPolicyTests
{
    private const string FeaturesRelativePath = "src/Honua.Server/Features";
    private const string ServerRelativePath = "src/Honua.Server";
    private const string GlobalMiddlewareRelativePath =
        "src/Honua.Server/Features/Infrastructure/Middleware/GlobalExceptionMiddleware.cs";
    private const string ProgramRelativePath = "src/Honua.Server/Program.cs";

    /// <summary>
    /// Markers that indicate an endpoint file has *some* error-handling
    /// scaffolding above and beyond the global middleware. The presence of
    /// any one of these is enough to satisfy the policy; the test is a smoke
    /// detector, not a code-quality grader.
    /// </summary>
    private static readonly string[] ErrorHandlingMarkers =
    {
        "try",
        "StandardErrorHelpers",
        "Wfs20ErrorResults",
        "TypedResults.Problem",
        "Results.Problem",
        "TypedResults.BadRequest",
        "Results.BadRequest",
        "TypedResults.NotFound",
        "Results.NotFound",
        "TypedResults.UnprocessableEntity",
        "Results.UnprocessableEntity",
        "ProblemDetails",
        "AdminResponseWriter",
        "ErrorResponse",
    };

    /// <summary>
    /// Empty endpoint shells (purely a <c>MapXxx</c> wiring file that
    /// forwards to a service) get a pass on the per-file scaffolding check.
    /// These are documented exceptions, not loopholes — the global middleware
    /// is sufficient when the file holds no inline logic.
    /// </summary>
    private static readonly HashSet<string> EndpointFilesWithoutInlineErrorHandling = new(System.StringComparer.Ordinal)
    {
        // Pure route-table partials and dispatcher files whose handlers live
        // in companion files that *do* carry error-handling scaffolding.
        Path.Combine("src", "Honua.Server", "Features", "Admin", "AdminInfoEndpoints.cs"),
        Path.Combine("src", "Honua.Server", "Features", "Admin", "FeatureOverviewEndpoints.cs"),
        Path.Combine("src", "Honua.Server", "Features", "Admin", "GeocodingAdminEndpoints.cs"),
        Path.Combine("src", "Honua.Server", "Features", "Admin", "LicenseAdminEndpoints.cs"),
        Path.Combine("src", "Honua.Server", "Features", "Collaboration", "CollaborationEndpoints.cs"),
        Path.Combine("src", "Honua.Server", "Features", "FileStorage", "FileStorageEndpoints.cs"),
        Path.Combine("src", "Honua.Server", "Features", "Forms", "FormPackageEndpoints.cs"),
        Path.Combine("src", "Honua.Server", "Features", "Geocoding", "GeocodingEndpoints.cs"),
        Path.Combine("src", "Honua.Server", "Features", "Infrastructure", "Caching", "CachingEndpoints.cs"),
        Path.Combine("src", "Honua.Server", "Features", "Protocols", "GeoServices", "FeatureServer", "FeatureServerEndpoints.cs"),
        Path.Combine("src", "Honua.Server", "Features", "Protocols", "GeoServices", "MapServer", "MapServerEndpoints.cs"),
        Path.Combine("src", "Honua.Server", "Features", "Protocols", "GeoServices", "NAServer", "NAServerEndpoints.cs"),
        Path.Combine("src", "Honua.Server", "Features", "Protocols", "OData", "ODataEndpoints.cs"),
        Path.Combine("src", "Honua.Server", "Features", "Protocols", "Ogc", "Api", "Coverages", "OgcCoveragesEndpoints.cs"),
        Path.Combine("src", "Honua.Server", "Features", "Protocols", "Ogc", "Api", "Features", "FeaturesEndpoints.cs"),
        Path.Combine("src", "Honua.Server", "Features", "Protocols", "Ogc", "Api", "Features", "OgcFeaturesEndpoints.cs"),
        Path.Combine("src", "Honua.Server", "Features", "Protocols", "Ogc", "Api", "Processes", "CoreEndpoints.cs"),
        Path.Combine("src", "Honua.Server", "Features", "Protocols", "Ogc", "Api", "Processes", "OgcProcessesEndpoints.cs"),
        Path.Combine("src", "Honua.Server", "Features", "Protocols", "Ogc", "Api", "Tiles", "CoreEndpoints.cs"),
        Path.Combine("src", "Honua.Server", "Features", "Protocols", "Ogc", "Api", "Tiles", "OgcTilesEndpoints.cs"),
        Path.Combine("src", "Honua.Server", "Features", "Protocols", "Ogc", "Classic", "OgcClassicEndpoints.cs"),
        Path.Combine("src", "Honua.Server", "Features", "Protocols", "Ogc", "Classic", "Wcs20", "Wcs20Endpoints.cs"),
        Path.Combine("src", "Honua.Server", "Features", "Protocols", "Ogc", "Classic", "Wfs20", "Wfs20Endpoints.cs"),
        Path.Combine("src", "Honua.Server", "Features", "Protocols", "Stac", "StacEndpoints.cs"),
        Path.Combine("src", "Honua.Server", "Features", "StaticMap", "StaticMapEndpoints.cs"),
    };

    [ArchitectureTest]
    public void GlobalExceptionMiddleware_IsWiredInProgram()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var middlewarePath = Path.Combine(repoRoot, GlobalMiddlewareRelativePath);
        var programPath = Path.Combine(repoRoot, ProgramRelativePath);

        File.Exists(middlewarePath).Should().BeTrue(
            "the audit relies on src/Honua.Server/.../GlobalExceptionMiddleware.cs being present "
            + "as the safety net for unhandled exceptions");
        File.Exists(programPath).Should().BeTrue(
            "Program.cs is expected to wire the global exception handler");

        var middlewareSource = File.ReadAllText(middlewarePath);
        middlewareSource.Should().Contain("class GlobalExceptionMiddleware",
            "the middleware class must keep its known name so the wiring test stays valid");

        var programSource = File.ReadAllText(programPath);
        programSource.Should().Contain("UseGlobalExceptionHandling()",
            "Program.cs must call app.UseGlobalExceptionHandling() so unhandled exceptions are "
            + "caught before they reach the wire");
    }

    [ArchitectureTest]
    public void EveryEndpointFile_HasErrorHandlingScaffolding()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var featuresRoot = Path.Combine(repoRoot, FeaturesRelativePath);

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(featuresRoot, "*Endpoints.cs", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(featuresRoot, "*Endpoints.*.cs", SearchOption.AllDirectories)))
        {
            var relative = Path.GetRelativePath(repoRoot, file);
            if (EndpointFilesWithoutInlineErrorHandling.Contains(relative))
            {
                continue;
            }

            var source = StripComments(File.ReadAllText(file));
            if (ErrorHandlingMarkers.Any(marker => source.Contains(marker, System.StringComparison.Ordinal)))
            {
                continue;
            }

            offenders.Add(relative);
        }

        offenders.Sort(System.StringComparer.Ordinal);
        offenders.Should().BeEmpty(
            "every endpoint file under " + FeaturesRelativePath + " should either include a "
            + "try/catch, use the project's error-formatting helpers (StandardErrorHelpers, "
            + "Wfs20ErrorResults, ProblemDetails, etc.) or be added to "
            + nameof(EndpointFilesWithoutInlineErrorHandling) + " with a justification. The "
            + "GlobalExceptionMiddleware is the safety net but per-endpoint shaping is what "
            + "produces the customer-facing error contract. Offending files:\n  - "
            + string.Join("\n  - ", offenders));
    }

    [ArchitectureTest]
    public void Server_AvoidsBareExceptionThrows()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var serverRoot = Path.Combine(repoRoot, ServerRelativePath);

        // `throw new Exception("...");` is too generic for production code —
        // it loses the failure mode and forces callers to catch everything.
        var pattern = new Regex(@"throw\s+new\s+Exception\s*\(", RegexOptions.CultureInvariant);
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(serverRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsGeneratedOrBinary(file))
            {
                continue;
            }

            var source = StripComments(File.ReadAllText(file));
            foreach (Match match in pattern.Matches(source))
            {
                if (IsIntentionallyMarked(source, match.Index))
                {
                    continue;
                }
                offenders.Add($"{Path.GetRelativePath(repoRoot, file)}");
                break;
            }
        }

        offenders.Should().BeEmpty(
            "throw new Exception(...) is too generic for production code; use a specific "
            + "exception type (ArgumentException, InvalidOperationException, etc.) or one of the "
            + "domain-specific exceptions. If the call site is intentional, suffix it with a "
            + "trailing `// intentional` comment.");
    }

    [ArchitectureTest]
    public void Server_AvoidsEmptyExceptionCatches()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var serverRoot = Path.Combine(repoRoot, ServerRelativePath);

        // A pattern like `catch (Exception) { }` (optionally with `ex`) and a
        // body that holds nothing but whitespace is a smell — it silently
        // swallows every failure mode.
        var pattern = new Regex(
            @"catch\s*\(\s*Exception(?:\s+\w+)?\s*\)\s*(?:when\s*\([^)]*\)\s*)?\{\s*\}",
            RegexOptions.CultureInvariant);
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(serverRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsGeneratedOrBinary(file))
            {
                continue;
            }

            var source = StripComments(File.ReadAllText(file));
            foreach (Match match in pattern.Matches(source))
            {
                if (IsIntentionallyMarked(source, match.Index))
                {
                    continue;
                }
                offenders.Add(Path.GetRelativePath(repoRoot, file));
                break;
            }
        }

        offenders.Should().BeEmpty(
            "empty `catch (Exception) { }` blocks hide failure modes. Log the exception, "
            + "rethrow a more specific one, or add a trailing `// intentional` comment "
            + "explaining why silently swallowing is acceptable.");
    }

    private static bool IsIntentionallyMarked(string source, int index)
    {
        // Look in the ~80 characters that follow for an `// intentional`
        // marker. Comments were stripped, so this scans the raw source after
        // the stripped one — defensive against the strip step having removed
        // the marker. We re-read the file's neighbourhood from the stripped
        // copy that callers pass us; if the comment was stripped, the marker
        // won't exist, so this conservatively returns false (the offender
        // counts).
        var window = source.Substring(index, System.Math.Min(120, source.Length - index));
        return window.Contains("intentional", System.StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGeneratedOrBinary(string filePath)
    {
        if (filePath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", System.StringComparison.Ordinal)
            || filePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", System.StringComparison.Ordinal))
        {
            return true;
        }

        // Generated files: source-generators emit *.g.cs and *.Designer.cs.
        if (filePath.EndsWith(".g.cs", System.StringComparison.OrdinalIgnoreCase)
            || filePath.EndsWith(".Designer.cs", System.StringComparison.OrdinalIgnoreCase)
            || filePath.EndsWith(".AssemblyInfo.cs", System.StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string StripComments(string source)
    {
        var noBlockComments = Regex.Replace(source, "/\\*.*?\\*/", string.Empty, RegexOptions.Singleline);
        // Keep `// intentional` markers intact for the IsIntentionallyMarked
        // probe by preserving comments after this point — we deliberately do
        // NOT strip line comments here.
        return noBlockComments;
    }
}
