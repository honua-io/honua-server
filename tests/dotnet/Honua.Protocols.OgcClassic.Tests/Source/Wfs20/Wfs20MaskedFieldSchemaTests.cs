// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

public sealed class Wfs20MaskedFieldSchemaTestsFixture : IAsyncLifetime
{
    public const string MaskedField = "notes";

    public WebAppFixture App { get; } = new WebAppFixture()
        .WithTestLicense(HonuaEdition.Pro)
        .ReplaceService<IFieldMaskSource>(new NotesFieldMaskSource());

    public Task InitializeAsync() => App.InitializeAsync();

    public Task DisposeAsync() => App.DisposeAsync();

    private sealed class NotesFieldMaskSource : IFieldMaskSource
    {
        public Task<ImmutableArray<string>> ResolveAsync(MetadataV2Object resource, CancellationToken cancellationToken = default)
            => Task.FromResult(ImmutableArray.Create(MaskedField));
    }
}

// The caller's field mask withholds "notes": the WFS schema and feature encodings may not name it.
[Collection("Database")]
[Protocol(TestProtocols.Wfs20)]
[Operation(Operations.Query)]
public sealed class Wfs20MaskedFieldSchemaTests(Wfs20MaskedFieldSchemaTestsFixture fixture)
    : IClassFixture<Wfs20MaskedFieldSchemaTestsFixture>
{
    [IntegrationTest]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "DescribeFeatureType")]
    public async Task DescribeFeatureType_OmitsMaskedField()
    {
        var body = await GetOkAsync("DescribeFeatureType", string.Empty);

        body.Should().Contain("\"name\"").And.NotContain("\"notes\"");
    }

    [IntegrationTheory]
    [InlineData("", ":notes")]
    [InlineData("&OUTPUTFORMAT=csv", ",notes")]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task GetFeature_OmitsMaskedField(string extra, string maskedMarker)
    {
        var body = await GetOkAsync("GetFeature", extra);

        body.Should().NotContain(maskedMarker);
    }

    [IntegrationTest]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task GetFeature_XmlMultiQuery_OmitsMaskedField()
    {
        const string request = """
            <wfs:GetFeature service="WFS" version="2.0.0" count="1" xmlns:wfs="http://www.opengis.net/wfs/2.0">
              <wfs:Query typeNames="pub-layer-0"/>
              <wfs:Query typeNames="pub-layer-0"/>
            </wfs:GetFeature>
            """;
        using var content = new StringContent(request, System.Text.Encoding.UTF8, "text/xml");
        using var response = await fixture.App.Client.PostAsync("/wfs", content);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain(":name").And.NotContain(":notes");
    }

    private async Task<string> GetOkAsync(string operation, string extra)
    {
        using var response = await fixture.App.Client.GetAsync(
            $"/wfs?SERVICE=WFS&VERSION=2.0.0&REQUEST={operation}&TYPENAMES=pub-layer-0&COUNT=1{extra}");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return body;
    }
}
