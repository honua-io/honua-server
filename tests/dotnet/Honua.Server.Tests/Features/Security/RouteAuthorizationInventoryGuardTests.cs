// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Honua.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Security;

[Collection("Database")]
public sealed class RouteAuthorizationInventoryGuardTests : IAsyncLifetime
{
    internal const string AllowListFileName = "route-authorization-allowlist.txt";
    private const string UpdateEnvironmentVariable = "HONUA_UPDATE_ROUTE_AUTHZ_ALLOWLIST";
    private const string ReportEnvironmentVariable = "HONUA_ROUTE_AUTHZ_REPORT";
    private const string UnverifiedMarker = "# UNVERIFIED";
    private const string AnonymousClass = "anonymous";
    private const string NoneClass = "none";

    private const string EventModeSentinelRoute = "/internal/control-plane/scheduled-tick";

    private static readonly string[] StaticallyReviewedEventModeRoutes =
    [
        "anonymous POST /internal/control-plane/backstop/sweep",
        "anonymous POST /internal/control-plane/events/batch-job-state-change",
    ];

    private readonly WebAppFixture _fixture = new();

    private readonly WebAppFixture _eventModeFixture = new WebAppFixture()
        .ConfigureWebHost(builder => builder.UseSetting("ControlPlane:TriggerMode", "Event"));

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        await _eventModeFixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _eventModeFixture.DisposeAsync();
        await _fixture.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void EveryRouteWithoutAuthorizationMetadata_IsOnTheCommittedAllowList()
    {
        var pollInventory = ClassifyDeployedRoutes(_fixture.Services);
        var eventInventory = ClassifyDeployedRoutes(_eventModeFixture.Services);
        eventInventory.Should().Contain(entry => entry.Pattern == EventModeSentinelRoute,
            "the Event-mode host must map the event-only control-plane routes, or this guard would not see them");
        var inventory = pollInventory.Union(eventInventory).ToArray();
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
            .Concat(StaticallyReviewedEventModeRoutes)
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

    [Fact]
    [Trait("Category", "Architecture")]
    public void EventModeControlPlaneMappingSection_RegistersOnlyTheReviewedRoutes()
    {
        var reviewed = StaticallyReviewedEventModeRoutes;

        var source = File.ReadAllText(Path.Join(
            RepositoryRoot(), "src", "Honua.Server", "Features", "ControlPlane", "ControlPlaneEventEndpoints.cs"));
        source.Should().Contain("MapGroup(\"/internal/control-plane\")", "the reviewed list assumes this group prefix");
        source.Should().Contain(".AllowAnonymous()", "the reviewed routes are anonymous at routing and token-checked in-handler");

        var mapped = Regex.Matches(source, @"\.Map(Get|Post|Put|Patch|Delete)\(\s*""(?<route>[^""]+)""")
            .Select(match => $"anonymous {match.Groups[1].Value.ToUpperInvariant()} /internal/control-plane{match.Groups["route"].Value}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        mapped.Should().Equal(reviewed,
            "every route registered by MapControlPlaneEventEndpoints must be reviewed; update this list and {0}", AllowListFileName);

        var allowList = ReadAllowList();
        reviewed.Should().AllSatisfy(line => allowList.Should().Contain(line,
            "Event-mode routes the runtime inventory cannot see are still listed in {0}", AllowListFileName));
        Regex.Count(source, @"CheckAuthorization\(").Should().BeGreaterThan(reviewed.Length,
            "each handler (plus the helper definition) must run the shared-secret token check");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the repository root (Honua.sln) must be reachable from the test output directory");
        return directory!.FullName;
    }

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

    private static void WriteAllowList(IEnumerable<string> lines)
    {
        var target = Path.Join(RepositoryRoot(), "tests", "dotnet", "Honua.Server.Tests", "Features", "Security", AllowListFileName);
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

    internal sealed record RouteAuthorizationEntry(string Method, string Pattern, string Classification, string Policies, string? AcceptsContentType)
    {
        public string AllowListLine => $"{Classification} {Method} {Pattern}";
    }
}
