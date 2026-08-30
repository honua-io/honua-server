// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class GitOpsDeployBackendCapabilityTests
{
    [Theory]
    [InlineData("kubernetes")]
    [InlineData("aws-ecs")]
    [InlineData("azure-container-apps")]
    public async Task GetCapabilitiesAsync_HandOffBackend_DoesNotClaimAutomaticRollback(string backendKind)
    {
        IDeployBackend backend = backendKind switch
        {
            "kubernetes" => new KubernetesGitOpsDeployBackend(
                NullLogger<KubernetesGitOpsDeployBackend>.Instance),
            "aws-ecs" => new AwsEcsGitOpsDeployBackend(
                NullLogger<AwsEcsGitOpsDeployBackend>.Instance),
            "azure-container-apps" => new AzureContainerAppsGitOpsDeployBackend(
                NullLogger<AzureContainerAppsGitOpsDeployBackend>.Instance),
            _ => throw new InvalidOperationException($"Unknown backend kind '{backendKind}'.")
        };

        var capabilities = await backend.GetCapabilitiesAsync();

        capabilities.SupportsRollback.Should().BeFalse(
            "GitOps hand-off rollback requires out-of-band manual intervention");
    }
}
