// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class DeployTargetKindContractTests
{
    private const string TargetId = "legacy-target";
    private const string Backend = "honua-test-backend";

    [UnitTest]
    public void Members_KeepTheirPersistedNumbers()
    {
        Enum.GetValues<DeployTargetKind>()
            .ToDictionary(kind => kind.ToString(), kind => (int)kind)
            .Should().Equal(new Dictionary<string, int>
            {
                ["Kubernetes"] = 0,
                ["AwsEcs"] = 1,
                ["AwsLambda"] = 3,
                ["SelfHostedRolling"] = 5,
                ["LocalCompose"] = 6
            });
    }

    [UnitTest]
    public void TargetDefinition_PersistsTheKindAsItsNumber()
    {
        var json = JsonSerializer.Serialize(Definition(DeployTargetKind.LocalCompose), ControlPlaneJsonContext.Default.DeployTargetDefinition);

        json.Should().Contain("\"targetKind\":6");
    }

    [UnitTheory]
    [InlineData(2)]
    [InlineData(4)]
    public void TargetDefinition_WithUnassignedKindNumber_DoesNotReadAsADefinedKind(int number)
    {
        var json = JsonSerializer.Serialize(Definition((DeployTargetKind)number), ControlPlaneJsonContext.Default.DeployTargetDefinition);

        var restored = JsonSerializer.Deserialize(json, ControlPlaneJsonContext.Default.DeployTargetDefinition)!;

        ((int)restored.TargetKind).Should().Be(number);
        Enum.IsDefined(restored.TargetKind).Should().BeFalse();
    }

    [UnitTheory]
    [InlineData("AzureContainerApps")]
    [InlineData("AzureFunctions")]
    [InlineData("NotATarget")]
    public void Bind_UnknownKindName_IsRefused(string name)
    {
        var bind = () => Bind(name);

        bind.Should().Throw<InvalidOperationException>();
    }

    [UnitTheory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(99)]
    public async Task PlanAsync_UnassignedKindNumber_FindsNoBackend(int number)
    {
        var options = Substitute.For<IOptionsMonitor<ControlPlaneOptions>>();
        options.CurrentValue.Returns(Bind(number.ToString(CultureInfo.InvariantCulture)));
        var service = new DeployWorkflowService(
            new ConfigurationDeployTargetRegistry(options),
            [],
            Enum.GetValues<DeployTargetKind>().Select(BackendFor),
            Substitute.For<IOperatorApprovalEvaluator>(),
            NullLogger<DeployWorkflowService>.Instance);

        var result = await service.PlanAsync(TargetId, "sha256:new", "sha256:old", parameterOverrides: null);

        result!.Capabilities.Should().BeNull();
        result.Plan.IsReadyToSubmit.Should().BeFalse();
        result.Plan.BlockingReasons.Should().Contain(reason => reason.StartsWith("No deploy backend is registered", StringComparison.Ordinal));
    }

    private static ControlPlaneOptions Bind(string targetKind)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ControlPlane:DeployTargets:0:TargetId"] = TargetId,
                ["ControlPlane:DeployTargets:0:TargetKind"] = targetKind,
                ["ControlPlane:DeployTargets:0:Backend"] = Backend
            })
            .Build();
        return configuration.GetSection(ControlPlaneOptions.SectionName).Get<ControlPlaneOptions>()!;
    }

    private static IDeployBackend BackendFor(DeployTargetKind kind)
    {
        var backend = Substitute.For<IDeployBackend>();
        backend.BackendName.Returns(Backend);
        backend.TargetKind.Returns(kind);
        return backend;
    }

    private static DeployTargetDefinition Definition(DeployTargetKind kind)
        => new()
        {
            TargetId = TargetId,
            TargetKind = kind,
            Backend = Backend,
            Environment = "production",
            TargetName = "honua-server"
        };
}
