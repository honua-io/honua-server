// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Security;

public sealed class InMemorySecureConnectionRegistryTests
{
    [UnitTest]
    public async Task GetConnectionAsync_StringOverloadWithCancellationToken_NonGuidId_FallsBackToNameLookup()
    {
        var registry = new InMemorySecureConnectionRegistry();
        var connection = new DataConnection { Name = "conn-1", Provider = "mysql" };
        await registry.CreateConnectionAsync(connection);

        var resolved = await registry.GetConnectionAsync("conn-1", CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.ConnectionId.Should().Be(connection.ConnectionId);
    }

    [UnitTest]
    public async Task GetConnectionAsync_StringOverloadWithCancellationToken_GuidId_ResolvesById()
    {
        var registry = new InMemorySecureConnectionRegistry();
        var connection = new DataConnection { Name = "sqlserver-primary", Provider = "sqlserver" };
        await registry.CreateConnectionAsync(connection);

        var resolved = await registry.GetConnectionAsync(
            connection.ConnectionId.ToString(),
            CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.Name.Should().Be("sqlserver-primary");
    }

    [UnitTest]
    public async Task GetConnectionAsync_StringOverloadWithCancellationToken_UnknownId_ReturnsNull()
    {
        var registry = new InMemorySecureConnectionRegistry();

        var resolved = await registry.GetConnectionAsync("does-not-exist", CancellationToken.None);

        resolved.Should().BeNull();
    }
}
