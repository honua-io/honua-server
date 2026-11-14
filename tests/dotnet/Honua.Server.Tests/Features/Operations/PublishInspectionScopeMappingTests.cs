// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Operations;

// OAuth callers reach the read-only Publish inspection tools with read authority; an unmapped
// operation is refused for every scope-governed caller.
public sealed class PublishInspectionScopeMappingTests
{
    [UnitTheory]
    [InlineData("admin.import.formats")]
    [InlineData("admin.import.limits")]
    [InlineData("admin.import.preview")]
    [InlineData("admin.import.preview-url")]
    [InlineData("admin.connections.tables.validate")]
    public void TryResolve_PublishInspection_RequiresOnlyReadAuthority(string operationId)
    {
        var request = new OperationRequest { OperationId = operationId };

        OperationScopeMapping.TryResolve(request, out var operation).Should().BeTrue();
        operation.Should().Be(OperatorOperation.Read);
        OperationScopeMapping.ExceedsScopeAuthority(request, new OperationPolicyContext
        {
            ScopeGoverned = true,
            RecognizedScopes = [OperatorScopeCatalog.Read],
        }).Should().BeFalse();
    }
}
