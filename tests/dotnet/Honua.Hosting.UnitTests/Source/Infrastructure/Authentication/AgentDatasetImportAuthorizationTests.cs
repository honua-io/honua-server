// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Infrastructure.Authentication;
using Xunit;

namespace Honua.Hosting.UnitTests.Infrastructure.Authentication;

public sealed class AgentDatasetImportAuthorizationTests
{
    [Theory]
    [InlineData("target-one/public/roads", true)]
    [InlineData("target-one/public/*", false)]
    [InlineData("*/public/roads", false)]
    [InlineData("target-one/public/../roads", false)]
    [InlineData("target-one/public/Roads", false)]
    [InlineData("target-one/public/roads/extra", false)]
    [InlineData("target-one/public/%72oads", false)]
    public void PermissionScope_IsExactAndCanonical(string scope, bool expected)
        => Assert.Equal(expected, AgentDatasetImportAuthorization.TryPermissionScope("agent:dataset-import:" + scope, out _));

    [Theory]
    [InlineData("dataset-import:target-one/public/roads", OperatorOperation.Create, true)]
    [InlineData("dataset-import:target-one/public/roads", OperatorOperation.Read, true)]
    [InlineData("dataset-import:target-one/public/other", OperatorOperation.Create, false)]
    [InlineData("dataset-import:target-two/public/roads", OperatorOperation.Create, false)]
    [InlineData("target-one", OperatorOperation.Create, false)]
    [InlineData("dataset-import:target-one/public/roads", OperatorOperation.Publish, false)]
    public void PermissionCeiling_DoesNotBecomeTargetWideCreateOrPublication(string resource, OperatorOperation operation, bool expected)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("api_key_id", Guid.NewGuid().ToString("D")),
            new Claim("permission", "agent:workspace"),
            new Claim("permission", "agent:dataset-import:target-one/public/roads")
        ], "test"));
        Assert.Equal(expected, AgentDatasetImportAuthorization.AllowsOperator(principal, new()
        {
            ResourceType = OperatorResourceType.Catalog,
            ResourceId = resource,
            Operation = operation
        }));
    }
}
