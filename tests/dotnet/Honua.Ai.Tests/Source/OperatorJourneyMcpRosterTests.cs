// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

public sealed class OperatorJourneyMcpRosterTests
{
    private static readonly IReadOnlyDictionary<string, OperationApprovalModel> WriteApprovalModels =
        new Dictionary<string, OperationApprovalModel>(StringComparer.Ordinal)
        {
            ["admin.connections.create"] = OperationApprovalModel.None,
            ["admin.import.upload-object"] = OperationApprovalModel.None,
            ["admin.import.upload-url"] = OperationApprovalModel.None,
            ["admin.layer.publish"] = OperationApprovalModel.OperatorGate,
            ["admin.services.access-policy.set"] = OperationApprovalModel.OperatorGate,
        };

    [UnitTest]
    public async Task Roster_DefaultComposition_AdvertisesToolsWithExpectedApprovalModels()
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
            if (WriteApprovalModels.TryGetValue(operationId, out var approvalModel))
            {
                mode.Should().Be("execute");
                descriptor.ApprovalModel.Should().Be(approvalModel, toolName);
            }
            else
            {
                mode.Should().Be("read");
                descriptor.Policy.SideEffectClass.Should().Be(OperationSideEffectClass.ReadOnly, toolName);
            }
        }

        foreach (var (operationId, approvalModel) in WriteApprovalModels)
        {
            descriptors[operationId].ApprovalModel.Should().Be(approvalModel, operationId);
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
