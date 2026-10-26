// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Security;

/// <summary>
/// Route-inventory authorization guard. Enumerates every <see cref="RouteEndpoint"/> the
/// running host registers and classifies it by its endpoint authorization metadata:
/// <list type="bullet">
/// <item><c>authorized</c>: carries <see cref="IAuthorizeData"/>, an <see cref="AuthorizationPolicy"/>
/// or <see cref="IAuthorizationRequirementData"/> and no <see cref="IAllowAnonymous"/>.</item>
/// <item><c>anonymous</c>: carries <see cref="IAllowAnonymous"/> (explicitly public).</item>
/// <item><c>none</c>: carries no authorization metadata at all.</item>
/// </list>
/// The host registers no fallback authorization policy, so a <c>none</c> route is reachable by
/// any caller unless its handler, an endpoint filter or the shared access-policy pipeline denies
/// it. Every <c>anonymous</c> and <c>none</c> route must therefore be listed in the committed
/// <c>route-authorization-allowlist.txt</c>, one per line, so a new unprotected route fails the
/// PR that adds it and its review diff names the route.
/// </summary>
/// <remarks>
/// The check is exact in both directions: an unlisted unprotected route fails, and a listed
/// route that is no longer deployed or now carries authorization metadata fails as stale.
/// Regenerate the file after an intended change with
/// <c>HONUA_UPDATE_ROUTE_AUTHZ_ALLOWLIST=1 dotnet test --filter RouteAuthorizationInventoryGuardTests</c>
/// and review the diff.
/// </remarks>
[Collection("Database")]
public sealed class RouteAuthorizationInventoryGuardTests : IAsyncLifetime
{
    internal const string AllowListFileName = "route-authorization-allowlist.txt";
    private const string UpdateEnvironmentVariable = "HONUA_UPDATE_ROUTE_AUTHZ_ALLOWLIST";
    private const string ReportEnvironmentVariable = "HONUA_ROUTE_AUTHZ_REPORT";
    private const string UnverifiedMarker = "# UNVERIFIED";
    private const string AnonymousClass = "anonymous";
    private const string NoneClass = "none";

    private readonly WebAppFixture _fixture = new();

    /// <inheritdoc />
    public Task InitializeAsync() => _fixture.InitializeAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    [Trait("Category", "Architecture")]
    public void EveryRouteWithoutAuthorizationMetadata_IsOnTheCommittedAllowList()
    {
        var inventory = ClassifyDeployedRoutes(_fixture.Services);
        inventory.Should().NotBeEmpty("the host must register routes for this guard to mean anything");
        inventory.Should().Contain(entry => entry.Classification == "authorized",
            "authorization metadata must be visible on the endpoint data source; if none is, the classifier is broken");

        var reportPath = Environment.GetEnvironmentVariable(ReportEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            File.WriteAllLines(reportPath, inventory.Select(entry => $"{entry.Classification}\t{entry.Method}\t{entry.Pattern}\t{entry.Policies}"));
        }

        var unprotected = inventory
            .Where(entry => entry.Classification != "authorized")
            .Select(entry => entry.AllowListLine)
            .ToHashSet(StringComparer.Ordinal);

        if (string.Equals(Environment.GetEnvironmentVariable(UpdateEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            WriteAllowList(unprotected);
            return;
        }

        var allowList = ReadAllowList();

        var unlisted = unprotected.Where(line => !allowList.Contains(line)).Order(StringComparer.Ordinal).ToArray();
        unlisted.Should().BeEmpty(
            "every route with no authorization metadata (or with [AllowAnonymous]) must be listed in {0}. " +
            "Add RequireAuthorization(...) to the route, or, if it is intentionally public or enforces access in " +
            "its handler, add these lines to the allow-list:\n{1}",
            AllowListFileName,
            string.Join('\n', unlisted));

        var stale = allowList.Where(line => !unprotected.Contains(line)).Order(StringComparer.Ordinal).ToArray();
        stale.Should().BeEmpty(
            "every line in {0} must name a deployed route that still lacks authorization metadata. " +
            "Remove these stale lines:\n{1}",
            AllowListFileName,
            string.Join('\n', stale));
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void AllowList_IsSortedUniqueAndOneRoutePerLine()
    {
        var lines = ReadAllowListLines();
        lines.Should().OnlyHaveUniqueItems();
        lines.Should().BeInAscendingOrder(StringComparer.Ordinal);
        lines.Should().AllSatisfy(line =>
        {
            // Route templates may contain spaces (GeoServices task names), so only the first two separators split.
            var parts = line.Split(' ', 3);
            parts.Should().HaveCount(3, "each entry is '<class> <METHOD> <route>' on one line: {0}", line);
            parts[0].Should().BeOneOf(AnonymousClass, NoneClass);
            parts[2].Should().StartWith("/");
        });
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void UnverifiedMarkers_EachAnnotateTheEntryThatFollows()
    {
        var raw = File.ReadAllLines(AllowListPath());
        for (var index = 0; index < raw.Length; index++)
        {
            if (!raw[index].StartsWith(UnverifiedMarker, StringComparison.Ordinal))
            {
                continue;
            }

            (index + 1 < raw.Length && IsEntry(raw[index + 1])).Should().BeTrue(
                "a '{0}' marker must sit on its own line directly above the allow-list entry it annotates (line {1})",
                UnverifiedMarker,
                index + 1);
        }
    }

    /// <summary>
    /// Classifies every deployed route endpoint by its authorization metadata.
    /// </summary>
    internal static IReadOnlyList<RouteAuthorizationEntry> ClassifyDeployedRoutes(IServiceProvider services)
    {
        var entries = new HashSet<RouteAuthorizationEntry>();
        foreach (var source in services.GetServices<EndpointDataSource>())
        {
            foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>())
            {
                var pattern = endpoint.RoutePattern.RawText;
                if (string.IsNullOrEmpty(pattern))
                {
                    continue;
                }

                if (!pattern.StartsWith('/'))
                {
                    pattern = "/" + pattern;
                }

                var classification = Classify(endpoint.Metadata);
                var policies = DescribePolicies(endpoint.Metadata);
                var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
                var accepts = endpoint.Metadata.GetMetadata<IAcceptsMetadata>()?.ContentTypes is { Count: > 0 } contentTypes ? contentTypes[0] : null;
                foreach (var method in methods is { Count: > 0 } ? methods : ["ANY"])
                {
                    entries.Add(new RouteAuthorizationEntry(method.ToUpperInvariant(), pattern, classification, policies, accepts));
                }
            }
        }

        return [.. entries.OrderBy(entry => entry.Pattern, StringComparer.Ordinal).ThenBy(entry => entry.Method, StringComparer.Ordinal)];
    }

    private static string Classify(EndpointMetadataCollection metadata)
    {
        if (metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            return AnonymousClass;
        }

        var hasAuthorization = metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0
            || metadata.GetOrderedMetadata<AuthorizationPolicy>().Count > 0
            || metadata.GetOrderedMetadata<IAuthorizationRequirementData>().Count > 0;
        return hasAuthorization ? "authorized" : NoneClass;
    }

    private static string DescribePolicies(EndpointMetadataCollection metadata)
    {
        var names = metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(data => data.Policy ?? (data.Roles is null ? "(authenticated)" : $"roles:{data.Roles}"))
            .Concat(metadata.GetOrderedMetadata<AuthorizationPolicy>().Select(_ => "(policy-object)"))
            .Concat(metadata.GetOrderedMetadata<IAuthorizationRequirementData>().Select(_ => "(requirements)"))
            .Distinct(StringComparer.Ordinal);
        return string.Join(',', names);
    }

    private static HashSet<string> ReadAllowList() => [.. ReadAllowListLines()];

    private static bool IsEntry(string line) => line.Trim().Length > 0 && !line.TrimStart().StartsWith('#');

    private static string AllowListPath()
    {
        var path = Path.Join(AppContext.BaseDirectory, "Features", "Security", AllowListFileName);
        File.Exists(path).Should().BeTrue("{0} must be copied to the test output directory", AllowListFileName);
        return path;
    }

    private static string[] ReadAllowListLines()
        => [.. File.ReadAllLines(AllowListPath()).Where(IsEntry).Select(line => line.Trim())];

    /// <summary>
    /// Rewrites the source-tree allow-list from the live inventory, keeping the file header and any
    /// comment lines (such as <c># UNVERIFIED</c> markers) that sit directly above a surviving entry.
    /// </summary>
    private static void WriteAllowList(IEnumerable<string> lines)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the allow-list is regenerated in the source tree next to this test");
        var target = Path.Join(directory!.FullName, "tests", "dotnet", "Honua.Server.Tests", "Features", "Security", AllowListFileName);
        var existing = File.ReadAllLines(target);
        var header = existing.TakeWhile(line => !IsEntry(line)).ToList();
        var annotations = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var pending = new List<string>();
        foreach (var line in existing.Skip(header.Count))
        {
            if (IsEntry(line))
            {
                if (pending.Count > 0)
                {
                    annotations[line.Trim()] = pending;
                    pending = [];
                }
            }
            else if (line.TrimStart().StartsWith('#'))
            {
                pending.Add(line);
            }
        }

        var output = new List<string>(header);
        foreach (var line in lines.Order(StringComparer.Ordinal))
        {
            if (annotations.TryGetValue(line, out var comments))
            {
                output.AddRange(comments);
            }

            output.Add(line);
        }

        File.WriteAllLines(target, output);
    }

    /// <summary>
    /// One deployed route (method + raw pattern), its authorization classification, the policies it names and
    /// the first request content type it accepts (routing answers 415 before authorization otherwise).
    /// </summary>
    internal sealed record RouteAuthorizationEntry(string Method, string Pattern, string Classification, string Policies, string? AcceptsContentType)
    {
        /// <summary>The allow-list line for this entry: <c>&lt;class&gt; &lt;METHOD&gt; &lt;route&gt;</c>.</summary>
        public string AllowListLine => $"{Classification} {Method} {Pattern}";
    }
}
