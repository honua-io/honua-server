// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
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
/// The closed operator roster is advertised by the default Postgres composition, and its four
/// writes are not operator-gated.
/// </summary>
public sealed class OperatorJourneyMcpRosterTests
{
    private static readonly string[] DirectWrites =
    [
        "admin.connections.create",
        "admin.import.upload-url",
        "admin.layer.publish",
        "admin.services.access-policy.set",
    ];

    [UnitTest]
    public async Task Roster_IsAdvertisedByTheDefaultComposition_AndWritesAreNotOperatorGated()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryPaths.Resolve(
            "docs", "gis", "data", "operator-journey-mcp-roster.v1.json")));
        var rows = document.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        rows.Select(row => row.GetProperty("operationId").GetString()).Should().OnlyHaveUniqueItems();

        await using var provider = BuildDefaultComposition();
        var surface = new McpDataAccessSurface(
            McpTaxonomyAlignmentTests.BuildTools(),
            [],
            NullLogger<McpDataAccessSurface>.Instance,
            toolSources: provider.GetServices<IMcpToolSource>());
        var published = (await surface.GetAllToolsAsync()).OfType<PublishedOperationTool>().ToArray();
        var byName = published.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        var catalog = await provider.GetRequiredService<IOperationCatalog>().GetSnapshotAsync();
        var descriptors = catalog.Operations.ToDictionary(descriptor => descriptor.OperationId, StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var toolName = row.GetProperty("toolName").GetString()!;
            var operationId = row.GetProperty("operationId").GetString()!;
            var mode = row.GetProperty("mode").GetString();
            byName.Should().ContainKey(toolName);
            toolName.Should().Be(PublishedOperationTool.ProjectName(operationId));
            var descriptor = descriptors[operationId];
            var fields = byName[toolName].Describe().InputSchema.GetProperty("properties").EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal);
            row.GetProperty("inputFields").EnumerateArray().Select(field => field.GetString())
                .Should().BeEquivalentTo(fields, $"{toolName} input fields must match the published schema");
            if (DirectWrites.Contains(operationId, StringComparer.Ordinal))
            {
                mode.Should().Be("execute");
                descriptor.ApprovalModel.Should().Be(OperationApprovalModel.None, toolName);
            }
            else
            {
                mode.Should().Be("read");
                descriptor.Policy.SideEffectClass.Should().Be(OperationSideEffectClass.ReadOnly, toolName);
            }
        }

        foreach (var operationId in DirectWrites)
        {
            descriptors[operationId].ApprovalModel.Should().Be(OperationApprovalModel.None, operationId);
        }

        descriptors["admin.connections.delete"].ApprovalModel.Should().Be(OperationApprovalModel.OperatorGate);
        descriptors["admin.layer.set-enabled"].ApprovalModel.Should().Be(OperationApprovalModel.OperatorGate);
        descriptors["admin.connections.update"].ApprovalModel.Should().Be(OperationApprovalModel.OperatorGate);
    }

    private static ServiceProvider BuildDefaultComposition()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<Honua.Core.Features.ControlPlane.Abstractions.IOperationProposalStore>());
        services.AddOperationsToolset(configuration, environment);
        services.AddAdminAccessOperations();
        McpServiceCollectionExtensions.AddMcpPublishedOperationTools(services, configuration);
        return services.BuildServiceProvider();
    }
}
