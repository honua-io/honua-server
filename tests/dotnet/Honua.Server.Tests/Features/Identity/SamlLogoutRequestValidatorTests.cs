// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Xml.Linq;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Identity.Saml;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;
using Xunit;

namespace Honua.Server.Tests.Features.Identity;

public sealed class SamlLogoutRequestValidatorTests
{
    private const string SloUrl = SamlTestAssertions.Audience + "/saml/slo";
    private static readonly DateTimeOffset _now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly SamlLogoutRequestValidator _validator = CreateValidator(SloUrl);

    [UnitTest]
    public void Validate_FreshRequestAddressedToThisSp_IsAccepted()
    {
        Assert.Null(_validator.Validate(LogoutRequest(_now)));
    }

    [UnitTest]
    public void TryConsume_SameRequestIdTwice_SecondIsRefused()
    {
        var request = LogoutRequest(_now, id: "_logout-once");

        Assert.True(_validator.TryConsume(request));
        Assert.False(_validator.TryConsume(request));
    }

    [UnitTest]
    public void Validate_DoesNotConsumeTheRequest()
    {
        var request = LogoutRequest(_now, id: "_logout-retry");

        Assert.Null(_validator.Validate(request));
        Assert.Null(_validator.Validate(request));
        Assert.True(_validator.TryConsume(request));
    }

    [UnitTheory]
    [InlineData(-421)]
    [InlineData(121)]
    public void Validate_IssueInstantOutsideWindow_IsRejected(int offsetSeconds)
    {
        var result = _validator.Validate(LogoutRequest(_now.AddSeconds(offsetSeconds)));

        Assert.Equal("SAML logout request is outside its validity window.", result);
    }

    [UnitTheory]
    [InlineData(-420)]
    [InlineData(120)]
    public void Validate_IssueInstantInsideWindow_IsAccepted(int offsetSeconds)
    {
        Assert.Null(_validator.Validate(LogoutRequest(_now.AddSeconds(offsetSeconds))));
    }

    [UnitTest]
    public void Validate_NotOnOrAfterPassed_IsRejected()
    {
        var request = LogoutRequest(_now);
        request.SetAttributeValue("NotOnOrAfter", Instant(_now.AddSeconds(-121)));

        Assert.Equal("SAML logout request has expired.", _validator.Validate(request));
    }

    [UnitTheory]
    [InlineData("https://other-sp.example.com/saml/slo")]
    [InlineData(null)]
    public void Validate_DestinationNotThisSp_IsRejected(string? destination)
    {
        var result = _validator.Validate(LogoutRequest(_now, destination: destination));

        Assert.Equal("SAML logout request Destination does not match the single logout service.", result);
    }

    [UnitTest]
    public void Validate_NoSingleLogoutUrlConfigured_IsRejected()
    {
        var validator = CreateValidator(singleLogoutServiceUrl: null);

        Assert.Equal("Single logout is not configured for this SP.", validator.Validate(LogoutRequest(_now, destination: null)));
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData(" ")]
    public void Validate_MissingId_IsRejected(string? id)
    {
        var request = LogoutRequest(_now);
        request.SetAttributeValue("ID", id);

        Assert.Equal("SAML logout request has no ID.", _validator.Validate(request));
    }

    [UnitTest]
    public void Validate_MissingIssueInstant_IsRejected()
    {
        var request = LogoutRequest(_now);
        request.SetAttributeValue("IssueInstant", null);

        Assert.Equal("SAML logout request has no valid IssueInstant.", _validator.Validate(request));
    }

    private static SamlLogoutRequestValidator CreateValidator(string? singleLogoutServiceUrl)
        => new(
            Options.Create(new SamlAuthenticationOptions
            {
                Enabled = true,
                SingleLogoutServiceUrl = singleLogoutServiceUrl,
            }),
            new SamlAssertionReplayCache(new InMemoryTokenReplayStore()),
            new FixedTimeProvider());

    private static XElement LogoutRequest(DateTimeOffset issuedAt, string? id = null, string? destination = SloUrl)
    {
        XNamespace samlp = "urn:oasis:names:tc:SAML:2.0:protocol";
        var request = new XElement(
            samlp + "LogoutRequest",
            new XAttribute("ID", id ?? "_logout" + Guid.NewGuid().ToString("N")),
            new XAttribute("Version", "2.0"),
            new XAttribute("IssueInstant", Instant(issuedAt)));
        request.SetAttributeValue("Destination", destination);
        return request;
    }

    private static string Instant(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
