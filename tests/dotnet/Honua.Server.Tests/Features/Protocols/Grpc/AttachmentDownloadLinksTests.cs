// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Protocols.Grpc;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

[Protocol(TestProtocols.Grpc)]
[Operation(Operations.Query)]
public sealed class AttachmentDownloadLinksTests
{
    private static readonly AttachmentDownloadTarget Target = new()
    {
        LayerId = 7,
        FeatureId = 42,
        AttachmentId = 23,
        StoragePath = "attachments/7/42/photo.jpg"
    };

    private readonly AttachmentDownloadLinks _links = new(new EphemeralDataProtectionProvider());

    [UnitTest]
    public void IssuedToken_ReadsBackItsTarget()
    {
        var token = _links.Issue(Target, DateTimeOffset.UtcNow.AddMinutes(5));

        _links.Read(token).Should().Be(Target);
        token.Should().NotContain("photo");
    }

    [UnitTest]
    public void ExpiredToken_IsRefused()
    {
        var token = _links.Issue(Target, DateTimeOffset.UtcNow.AddSeconds(-1));

        _links.Read(token).Should().BeNull();
    }

    [UnitTest]
    public void TokenFromAnotherKeyRingOrAlteredOrMalformed_IsRefused()
    {
        var token = _links.Issue(Target, DateTimeOffset.UtcNow.AddMinutes(5));
        var other = new AttachmentDownloadLinks(new EphemeralDataProtectionProvider());
        var altered = token[..5] + (token[5] == 'A' ? 'B' : 'A') + token[6..];

        other.Read(token).Should().BeNull();
        _links.Read(altered).Should().BeNull();
        _links.Read("not-a-token").Should().BeNull();
    }

    [UnitTest]
    public void CreateUrl_HttpsRequestNeverGetsPlainHttpLink()
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(5);

        _links.CreateUrl(Request("https", "https://maps.example.test/"), Target, expires)
            .Should().StartWith("https://maps.example.test/api/v1/attachments/downloads/");
        _links.CreateUrl(Request("http", "http://localhost:8080"), Target, expires)
            .Should().StartWith("http://localhost:8080/api/v1/attachments/downloads/");
        _links.CreateUrl(Request("https", "http://maps.example.test"), Target, expires).Should().BeNull();
    }

    private static DefaultHttpContext Request(string scheme, string publicBaseUrl)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Public:BaseUrl"] = publicBaseUrl })
            .Build();
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton<IConfiguration>(configuration).BuildServiceProvider()
        };
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString("maps.example.test");
        return context;
    }
}
