// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;

namespace Honua.Core.Tests.Features.Operations;

public sealed class OperationScopeMappingTests
{
    [Theory]
    [InlineData("admin.layer.publish", OperatorOperation.Publish)]
    [InlineData("admin.connections.create", OperatorOperation.Create)]
    [InlineData("admin.import.upload", OperatorOperation.Create)]
    [InlineData("admin.layer.set-enabled", OperatorOperation.Update)]
    [InlineData("admin.connections.features.refresh", OperatorOperation.Update)]
    [InlineData("admin.connections.delete", OperatorOperation.Delete)]
    [InlineData("admin.import.jobs.cancel", OperatorOperation.Delete)]
    public void TryResolve_AdminApprovalReplay_UsesSemanticOperation(
        string operationId,
        OperatorOperation expected)
    {
        var resolved = OperationScopeMapping.TryResolve(
            new OperationRequest { OperationId = operationId },
            out var operation);

        resolved.Should().BeTrue();
        operation.Should().Be(expected);
    }

    [Fact]
    public void TryResolve_UnknownAdminOperation_FailsClosed()
    {
        OperationScopeMapping.TryResolve(
                new OperationRequest { OperationId = "admin.unknown.mutation" },
                out _)
            .Should().BeFalse();
    }
}
