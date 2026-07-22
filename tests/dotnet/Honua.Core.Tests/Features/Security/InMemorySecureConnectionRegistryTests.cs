// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Security;

/// <summary>
/// Unit tests for <see cref="InMemorySecureConnectionRegistry"/> (honua-server#2947 review
/// finding): <c>FeatureProviderQueryRouter</c> resolves a storage binding's connection by
/// passing the Metadata V2 connection id string into <c>GetConnectionAsync(string,
/// CancellationToken)</c>. Those ids are not required to be GUIDs (test graphs and metadata
/// commonly use names like <c>conn-1</c>), so this registry must fall back to a name lookup
/// the same way <c>PostgresSecureConnectionRegistry.GetConnectionAsync(string,
/// CancellationToken)</c> does, instead of returning null for any non-GUID id.
/// </summary>
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
