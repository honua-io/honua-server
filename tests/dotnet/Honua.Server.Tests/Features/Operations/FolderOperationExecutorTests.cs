// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed class FolderOperationExecutorTests
{
    [UnitTheory]
    [InlineData("folder.create")]
    [InlineData("folder.update")]
    [InlineData("folder.delete")]
    [InlineData("service.move")]
    public async Task SubmitAsync_WithoutHttpContext_ForgedApprovalRefusesWithoutTransportOrCatalogAccess(string operationId)
    {
        var proposals = Substitute.For<IOperationProposalStore>();
        var catalog = Substitute.For<IMetadataV2GraphStore>();
        var keys = Substitute.For<IAdminApiKeyStore>();
        var clients = Substitute.For<IHttpClientFactory>();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns("Test");
        var services = new ServiceCollection().AddLogging()
            .AddSingleton(proposals).AddSingleton(catalog).AddSingleton(keys).AddSingleton(clients);
        services.AddOperationsToolset(new ConfigurationBuilder().Build(), environment);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredKeyedService<IOperationExecutor>(operationId);

        var handle = await executor.SubmitAsync(new OperationRequest
        {
            OperationId = operationId, ServiceName = "parcels",
            Parameters = new Dictionary<string, string?>
            {
                ["name"] = "survey", ["id"] = "folder", ["folderId"] = null, ["If-Match"] = "revision"
            }
        }, new OperationPolicyContext
        {
            ApprovedProposalId = "forged", ApprovedPlanHash = "forged", PrincipalId = "requester",
            OperationInstanceId = "instance", CorrelationId = "correlation"
        });

        handle.Status.Should().Be(OperationHandleStatus.Failed);
        handle.Result!.Details["statusCode"].Should().Be("403");
        handle.OperationInstanceId.Should().Be("instance");
        handle.CorrelationId.Should().Be("correlation");
        keys.ReceivedCalls().Should().BeEmpty();
        clients.ReceivedCalls().Should().BeEmpty();
        catalog.ReceivedCalls().Should().BeEmpty();
    }
}
