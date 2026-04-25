// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Core.Features.Security.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Admin;

/// <summary>
/// Regression tests for connection discovery exception-to-status-code mapping.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.TableDiscovery)]
public sealed class ConnectionDiscoveryExceptionMappingTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task GetConnectionTables_WhenResolverThrowsUnexpectedInvalidOperation_ReturnsInternalServerError()
    {
        var fixture = new WebAppFixture()
            .ReplaceService<ISecureConnectionResolver>(CreateResolverThatThrows(
                () => new InvalidOperationException("unexpected resolver failure")));
        await fixture.InitializeAsync();

        try
        {
            var response = await fixture.CreateAdminClient().GetAsync(
                $"/api/v1/admin/connections/{Guid.NewGuid()}/tables");

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            var payload = await response.Content.ReadAsStringAsync();
            payload.Should().NotContain("unexpected resolver failure");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    public async Task GetConnectionTables_WhenResolverThrowsResourceNotFound_ReturnsNotFound()
    {
        var fixture = new WebAppFixture()
            .ReplaceService<ISecureConnectionResolver>(CreateResolverThatThrows(
                () => new ResourceNotFoundException("Connection not found.")));
        await fixture.InitializeAsync();

        try
        {
            var response = await fixture.CreateAdminClient().GetAsync(
                $"/api/v1/admin/connections/{Guid.NewGuid()}/tables");

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            var payload = await response.Content.ReadAsStringAsync();
            payload.Should().ContainEquivalentOf("connection not found");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static ISecureConnectionResolver CreateResolverThatThrows(Func<Exception> exceptionFactory)
    {
        var resolver = Substitute.For<ISecureConnectionResolver>();
        resolver.ResolveConnectionStringAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<string>(exceptionFactory()));
        resolver.ResolveConnectionStringAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<string>(exceptionFactory()));
        return resolver;
    }
}
