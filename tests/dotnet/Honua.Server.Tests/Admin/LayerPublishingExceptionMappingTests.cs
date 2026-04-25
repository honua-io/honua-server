// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Admin;

/// <summary>
/// Regression tests for layer publishing exception-to-status-code mapping.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class LayerPublishingExceptionMappingTests
{
    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task PublishLayer_WhenResolverThrowsUnexpectedInvalidOperation_ReturnsInternalServerError()
    {
        var fixture = new WebAppFixture()
            .ReplaceService<ISecureConnectionResolver>(CreateResolverThatThrows(
                () => new InvalidOperationException("unexpected resolver failure")));
        await fixture.InitializeAsync();

        try
        {
            var response = await fixture.CreateAdminClient().PostAsJsonAsync(
                $"/api/v1/admin/connections/{Guid.NewGuid()}/layers",
                CreatePublishRequest());

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
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task PublishLayer_WhenResolverThrowsResourceNotFound_ReturnsNotFound()
    {
        var fixture = new WebAppFixture()
            .ReplaceService<ISecureConnectionResolver>(CreateResolverThatThrows(
                () => new ResourceNotFoundException("Connection not found.")));
        await fixture.InitializeAsync();

        try
        {
            var response = await fixture.CreateAdminClient().PostAsJsonAsync(
                $"/api/v1/admin/connections/{Guid.NewGuid()}/layers",
                CreatePublishRequest());

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            var payload = await response.Content.ReadAsStringAsync();
            payload.Should().ContainEquivalentOf("requested resource was not found");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /api/v1/admin/connections/{id}/layers")]
    public async Task ListLayers_WhenResolverThrowsUnexpectedInvalidOperation_ReturnsInternalServerError()
    {
        var fixture = new WebAppFixture()
            .ReplaceService<ISecureConnectionResolver>(CreateResolverThatThrows(
                () => new InvalidOperationException("unexpected resolver failure")));
        await fixture.InitializeAsync();

        try
        {
            var response = await fixture.CreateAdminClient().GetAsync(
                $"/api/v1/admin/connections/{Guid.NewGuid()}/layers");

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
    [Operation(Operations.Query)]
    [Endpoint("GET /api/v1/admin/connections/{id}/layers")]
    public async Task ListLayers_WhenResolverThrowsResourceNotFound_ReturnsNotFound()
    {
        var fixture = new WebAppFixture()
            .ReplaceService<ISecureConnectionResolver>(CreateResolverThatThrows(
                () => new ResourceNotFoundException("Connection not found.")));
        await fixture.InitializeAsync();

        try
        {
            var response = await fixture.CreateAdminClient().GetAsync(
                $"/api/v1/admin/connections/{Guid.NewGuid()}/layers");

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            var payload = await response.Content.ReadAsStringAsync();
            payload.Should().ContainEquivalentOf("requested resource was not found");
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

    private static PublishLayerRequest CreatePublishRequest()
    {
        return new PublishLayerRequest
        {
            Schema = "public",
            Table = "test_table",
            LayerName = "Test Layer",
            ServiceName = "test",
            Enabled = true
        };
    }
}
