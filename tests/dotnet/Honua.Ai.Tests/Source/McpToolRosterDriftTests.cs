// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Ai.Protocols.Mcp.Views;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Operations.Services;
using Honua.Server.Features.Operations;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Drift gate and opt-in emitter for <c>docs/gis/data/mcp-tool-roster.v1.json</c>, the canonical
/// <c>/mcp</c> tool roster that downstream repositories (sdk-js certification, honua-release
/// expected tools, the geospatial-mcp index) gate their <c>tools/list</c> parity on.
/// </summary>
/// <remarks>
/// The roster body is generated from the hand-authored <see cref="IMcpTool"/> registrations,
/// the audited Admin projection manifest plus the closed operator roster (minus
/// <see cref="AdminMcpOperationExclusions"/>), the Admin catalog membership that only registers
/// with a durable proposal store, and <see cref="McpWorkflowViewCatalog"/>. The provenance fields
/// (<c>serverSha</c>, <c>generatedAt</c>) record the commit the body was last regenerated from and
/// are only restamped when the body changes, so an unchanged roster never produces a diff.
/// </remarks>
public sealed class McpToolRosterDriftTests
{
    private const string EmitVariable = "HONUA_EMIT_MCP_TOOL_ROSTER";
    private const string ServerShaVariable = "HONUA_MCP_TOOL_ROSTER_SERVER_SHA";
    private const string RosterFile = "mcp-tool-roster.v1.json";
    private const string RegenerateCommand = "bash scripts/generate-mcp-tool-roster.sh";

    /// <summary>Tool names the server deliberately no longer advertises.</summary>
    internal static readonly string[] RetiredToolNames = ["honua_propose_operation"];

    [UnitTest]
    public void CommittedRoster_MatchesTheGeneratedRoster()
    {
        var committed = ReadCommitted();
        using var document = JsonDocument.Parse(committed);
        var serverSha = document.RootElement.GetProperty("serverSha").GetString()!;
        var generatedAt = document.RootElement.GetProperty("generatedAt").GetString()!;

        serverSha.Should().MatchRegex("^[0-9a-f]{40}$", "serverSha records the full commit the roster was generated from");
        DateTimeOffset.TryParseExact(
                generatedAt, "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _)
            .Should().BeTrue("generatedAt is a UTC ISO-8601 timestamp");

        var expected = Render(Generate(), serverSha, generatedAt);
        if (!string.Equals(committed, expected, StringComparison.Ordinal))
        {
            Assert.Fail(
                $"docs/gis/data/{RosterFile} drifted from the composed /mcp roster. Regenerate it with "
                + $"`{RegenerateCommand}` and commit the result.\n{LineDiff(committed, expected)}");
        }
    }

    [UnitTest]
    public void DefaultView_IsServedOnlyFromHandAuthoredTools()
    {
        var roster = Generate();
        roster.Views[McpWorkflowViewCatalog.DefaultViewName].Should().NotBeEmpty();
        roster.Views[McpWorkflowViewCatalog.DefaultViewName].Should().BeSubsetOf(roster.Static,
            "the bounded default view never carries projected Admin tools");
    }

    [UnitTest]
    public void RetiredTools_AreNotAdvertised()
    {
        var roster = Generate();
        var advertised = roster.Static
            .Concat(roster.ProjectedAdmin)
            .Concat(roster.Views.Values.SelectMany(static names => names))
            .ToHashSet(StringComparer.Ordinal);
        advertised.Should().NotIntersectWith(roster.Retired);
        roster.Retired.Should().Equal(RetiredToolNames.Order(StringComparer.Ordinal));
    }

    [UnitTest]
    public void DurableControlPlaneTools_AreProjectedAdminTools()
    {
        var roster = Generate();
        roster.RequiresDurableControlPlane.Should().NotBeEmpty();
        roster.RequiresDurableControlPlane.Should().BeSubsetOf(roster.ProjectedAdmin);
    }

    [UnitTest]
    public async Task Roster_MatchesTheComposedServer_WithAndWithoutADurableProposalStore()
    {
        // The roster is derived from committed manifests and catalog membership; prove it is
        // what the production composition actually publishes in both topologies.
        var roster = Generate();

        await using (var durable = BuildDefaultComposition(withProposalStore: true))
        {
            (await PublishedToolNamesAsync(durable)).Should().Equal(roster.ProjectedAdmin,
                "a durable control plane publishes exactly the projected Admin roster");
        }

        await using (var withoutStore = BuildDefaultComposition(withProposalStore: false))
        {
            (await PublishedToolNamesAsync(withoutStore)).Should().Equal(
                roster.ProjectedAdmin.Except(roster.RequiresDurableControlPlane, StringComparer.Ordinal),
                "without a proposal store exactly the durable-control-plane names drop out");
        }

        McpTaxonomyAlignmentTests.BuildTools().Select(static tool => tool.Name).Order(StringComparer.Ordinal)
            .Should().Equal(roster.Static);
    }

    [UnitTest]
    public void McpToolRoster_EmitsWhenExplicitlyRequested()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EmitVariable), "1", StringComparison.Ordinal))
            return;

        var roster = Generate();
        var path = RepositoryPaths.Resolve("docs", "gis", "data", RosterFile);
        if (File.Exists(path))
        {
            // Keep the provenance stamp stable when the roster body has not changed.
            var committed = File.ReadAllText(path);
            using var document = JsonDocument.Parse(committed);
            var serverSha = document.RootElement.GetProperty("serverSha").GetString()!;
            var generatedAt = document.RootElement.GetProperty("generatedAt").GetString()!;
            if (string.Equals(committed, Render(roster, serverSha, generatedAt), StringComparison.Ordinal))
                return;
        }

        var sha = Environment.GetEnvironmentVariable(ServerShaVariable);
        if (string.IsNullOrWhiteSpace(sha))
            sha = GitHead();
        var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        File.WriteAllText(path, Render(roster, sha.Trim(), now), new UTF8Encoding(false));
    }

    internal sealed record Roster(
        string[] Static,
        string[] ProjectedAdmin,
        string[] RequiresDurableControlPlane,
        IReadOnlyDictionary<string, string[]> Views,
        string[] Retired);

    internal static Roster Generate()
    {
        var staticNames = McpTaxonomyAlignmentTests.BuildTools()
            .Select(static tool => tool.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var projected = ReadToolRows("admin-mcp-projection-manifest.json", "operations")
            .Concat(ReadToolRows("operator-journey-mcp-roster.v1.json", "tools"))
            .Where(static row => !AdminMcpOperationExclusions.ContainsOperation(row.OperationId))
            .Where(static row => !AdminMcpOperationExclusions.All.Any(
                exclusion => string.Equals(exclusion.ToolName, row.ToolName, StringComparison.Ordinal)))
            .DistinctBy(static row => row.ToolName, StringComparer.Ordinal)
            .OrderBy(static row => row.ToolName, StringComparer.Ordinal)
            .ToArray();

        // The Admin API and connect/import families register only when a durable
        // IOperationProposalStore is composed (OperationsServiceCollectionExtensions).
        var durableOperationIds = AdminApiOperationCatalog.Definitions.Select(static definition => definition.OperationId)
            .Concat(AdminConnectImportOperationCatalog.Definitions.Select(static definition => definition.OperationId))
            .ToHashSet(StringComparer.Ordinal);
        var requiresDurable = projected
            .Where(row => durableOperationIds.Contains(row.OperationId))
            .Select(static row => row.ToolName)
            .ToArray();

        var advertised = staticNames.Concat(projected.Select(static row => row.ToolName)).ToArray();
        var views = McpWorkflowViewCatalog.All
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(
                static pair => pair.Key,
                pair => advertised
                    .Where(name => pair.Value.FindStageIndex(name) >= 0)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);

        return new Roster(
            staticNames,
            projected.Select(static row => row.ToolName).ToArray(),
            requiresDurable,
            views,
            RetiredToolNames.Order(StringComparer.Ordinal).ToArray());
    }

    internal static string Render(Roster roster, string serverSha, string generatedAt)
    {
        var views = new JsonObject();
        foreach (var (name, members) in roster.Views)
            views[name] = ToArray(members);

        var root = new JsonObject
        {
            ["$schema"] = "./mcp-tool-roster.v1.schema.json",
            ["schemaVersion"] = 1,
            ["serverSha"] = serverSha,
            ["generatedAt"] = generatedAt,
            ["regenerate"] = RegenerateCommand,
            ["static"] = ToArray(roster.Static),
            ["projectedAdmin"] = ToArray(roster.ProjectedAdmin),
            ["requiresDurableControlPlane"] = ToArray(roster.RequiresDurableControlPlane),
            ["views"] = views,
            ["retired"] = ToArray(roster.Retired),
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, NewLine = "\n" }) + "\n";
    }

    private static JsonArray ToArray(IEnumerable<string> names) =>
        new(names.Select(static name => (JsonNode?)JsonValue.Create(name)).ToArray());

    private sealed record ToolRow(string ToolName, string OperationId);

    private static ToolRow[] ReadToolRows(string fileName, string arrayProperty)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryPaths.Resolve("docs", "gis", "data", fileName)));
        return document.RootElement.GetProperty(arrayProperty).EnumerateArray()
            .Select(static row => new ToolRow(
                row.GetProperty("toolName").GetString()!,
                row.GetProperty("operationId").GetString()!))
            .ToArray();
    }

    private static string ReadCommitted() =>
        File.ReadAllText(RepositoryPaths.Resolve("docs", "gis", "data", RosterFile));

    private static string GitHead()
    {
        using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
        {
            WorkingDirectory = RepositoryPaths.Resolve("docs"),
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output;
    }

    private static string LineDiff(string committed, string expected)
    {
        var committedLines = committed.Split('\n');
        var expectedLines = expected.Split('\n');
        var removed = committedLines.Except(expectedLines, StringComparer.Ordinal).Select(static line => "- " + line);
        var added = expectedLines.Except(committedLines, StringComparer.Ordinal).Select(static line => "+ " + line);
        var lines = removed.Concat(added).ToArray();
        return lines.Length == 0
            ? "(same lines, different order or line count)"
            : "--- committed\n+++ generated\n" + string.Join('\n', lines);
    }

    private static ServiceProvider BuildDefaultComposition(bool withProposalStore)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        var services = new ServiceCollection();
        services.AddLogging();
        if (withProposalStore)
            services.AddSingleton(Substitute.For<IOperationProposalStore>());
        services.AddOperationsToolset(configuration, environment);
        services.AddAdminAccessOperations();
        McpServiceCollectionExtensions.AddMcpPublishedOperationTools(services, configuration);
        return services.BuildServiceProvider();
    }

    private static async Task<string[]> PublishedToolNamesAsync(IServiceProvider provider)
    {
        var surface = new McpDataAccessSurface(
            McpTaxonomyAlignmentTests.BuildTools(),
            [],
            NullLogger<McpDataAccessSurface>.Instance,
            toolSources: provider.GetServices<IMcpToolSource>());
        return (await surface.GetAllToolsAsync())
            .OfType<PublishedOperationTool>()
            .Select(static tool => tool.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
