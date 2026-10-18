// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.AgentWorkspace;

public sealed class DatasetImportAuthorityAdapterTests
{
    [UnitTheory]
    [InlineData("null-call")]
    [InlineData("null-request")]
    [InlineData("workspace")]
    [InlineData("task")]
    [InlineData("revision")]
    [InlineData("null-hash")]
    [InlineData("uppercase-hash")]
    [InlineData("oversize-hash")]
    [InlineData("renewal-id")]
    [InlineData("authority-revision")]
    public async Task InvalidRenewalIsRejectedBeforeResolvingDescriptorOrStore(string input)
    {
        var descriptorResolved = false;
        using var services = new ServiceCollection().AddSingleton<IDatasetImportAuthorityDescriptorReader>(_ =>
        {
            descriptorResolved = true;
            return Substitute.For<IDatasetImportAuthorityDescriptorReader>();
        }).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        var store = Substitute.For<IAgentWorkspaceStore>();
        var request = new RenewDatasetImportAuthority("renewal-1", 1, new string('a', 64));
        var canonicalCall = new RenewDatasetImportAuthorityCall("workspace-1", "import-task-1", 1, request);
        RenewDatasetImportAuthorityCall? call = input switch
        {
            "null-call" => null,
            "null-request" => canonicalCall with { Request = null! },
            "workspace" => canonicalCall with { WorkspaceId = "invalid/workspace" },
            "task" => canonicalCall with { TaskId = "invalid/task" },
            "revision" => canonicalCall with { Revision = 0 },
            "null-hash" => canonicalCall with { Request = request with { TaskContentHash = null! } },
            "uppercase-hash" => canonicalCall with { Request = request with { TaskContentHash = new string('A', 64) } },
            "oversize-hash" => canonicalCall with { Request = request with { TaskContentHash = new string('a', 65) } },
            "renewal-id" => canonicalCall with { Request = request with { RenewalId = "invalid/id" } },
            _ => canonicalCall with { Request = request with { ExpectedAuthorityRevision = 0 } },
        };
        var result = await DatasetImportTaskEndpoints.RenewAuthorityAsync(context, store, call);
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(400);
        descriptorResolved.Should().BeFalse();
        await store.DidNotReceive().GetAsync(Arg.Any<string>());
        await store.DidNotReceive().SaveAsync(Arg.Any<AgentWorkspaceDocument>());
    }

    [UnitTest]
    public async Task InvalidReadIsRejectedBeforeResolvingDescriptor()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        ((IStatusCodeHttpResult)await DatasetImportTaskEndpoints.ReadAuthorityAsync(context, null)).StatusCode.Should().Be(400);
        ((IStatusCodeHttpResult)await DatasetImportTaskEndpoints.ReadAuthorityAsync(context,
            new("workspace-1", "import-task-1", 0, new string('a', 64)))).StatusCode.Should().Be(400);
    }
}
