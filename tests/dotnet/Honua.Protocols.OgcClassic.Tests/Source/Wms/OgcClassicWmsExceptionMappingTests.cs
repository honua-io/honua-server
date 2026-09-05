// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wms;

/// <summary>
/// Verifies that an unexpected WMS failure remains an HTTP failure while retaining
/// the protocol-level ServiceExceptionReport payload.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Wms13)]
public sealed class OgcClassicWmsExceptionMappingTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ReplaceService<IMetadataV2GraphProvider>(new ThrowingMetadataV2GraphProvider());

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Wms)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wms_GetCapabilities_WhenMetadataProviderFails_Returns500ServiceExceptionReport()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetCapabilities");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/xml");
        content.Should().Contain("ServiceExceptionReport");
        content.Should().Contain("code=\"NoApplicableCode\"");
    }

    private sealed class ThrowingMetadataV2GraphProvider : IMetadataV2GraphProvider
    {
        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("metadata provider failure for WMS exception mapping test");

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(
            long revision,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("metadata provider failure for WMS exception mapping test");
    }
}
