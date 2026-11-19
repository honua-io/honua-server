// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml.Linq;
using Honua.Server.Features.Identity.Saml;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.WebUtilities;

namespace Honua.Server.Tests.Features.Identity;

[Protocol(TestProtocols.Admin)]
public sealed class SamlSignInRequestTests
{
    private const string SamlProtocolNs = "urn:oasis:names:tc:SAML:2.0:protocol";
    private const string SamlAssertionNs = "urn:oasis:names:tc:SAML:2.0:assertion";

    private static SamlAuthenticationOptions CreateOptions() => new()
    {
        Enabled = true,
        EntityId = SamlTestAssertions.Audience,
        IdpEntityId = SamlTestAssertions.Issuer,
        AssertionConsumerServiceUrl = SamlTestAssertions.AcsUrl,
        IdpSingleSignOnServiceUrl = SamlSignInTestFlow.IdpSignOnUrl,
    };

    [UnitTest]
    public void Create_WritesTheAuthnRequestFields()
    {
        var issued = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

        var request = SamlAuthnRequestBuilder.Create(CreateOptions(), issued);

        var root = XDocument.Parse(request.Xml).Root!;
        Assert.Equal(XName.Get("AuthnRequest", SamlProtocolNs), root.Name);
        Assert.Matches("^_[0-9a-f]{32}$", request.Id);
        Assert.Equal(request.Id, root.Attribute("ID")!.Value);
        Assert.Equal("2.0", root.Attribute("Version")!.Value);
        Assert.Equal("2026-11-18T12:00:00Z", root.Attribute("IssueInstant")!.Value);
        Assert.Equal(SamlSignInTestFlow.IdpSignOnUrl, root.Attribute("Destination")!.Value);
        Assert.Equal(SamlTestAssertions.AcsUrl, root.Attribute("AssertionConsumerServiceURL")!.Value);
        Assert.Equal("urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST", root.Attribute("ProtocolBinding")!.Value);
        Assert.Equal(SamlTestAssertions.Audience, root.Element(XName.Get("Issuer", SamlAssertionNs))!.Value);
    }

    [UnitTest]
    public void Create_IssuesAFreshIdEachTime()
    {
        var first = SamlAuthnRequestBuilder.Create(CreateOptions(), DateTimeOffset.UtcNow);
        var second = SamlAuthnRequestBuilder.Create(CreateOptions(), DateTimeOffset.UtcNow);

        Assert.NotEqual(first.Id, second.Id);
    }

    [UnitTest]
    public void EncodeForRedirect_RoundTripsThroughRawInflate()
    {
        var request = SamlAuthnRequestBuilder.Create(CreateOptions(), DateTimeOffset.UtcNow);

        var encoded = SamlAuthnRequestBuilder.EncodeForRedirect(request.Xml);

        Assert.Equal(request.Xml, SamlSignInTestFlow.DecodeRedirectRequest(encoded));
    }

    [UnitTest]
    public void BuildRedirectUrl_KeepsTheIdpQueryAndAddsTheRequest()
    {
        var url = SamlAuthnRequestBuilder.BuildRedirectUrl("https://idp.example.com/sso?tenant=a", "a+b/c=");

        var uri = new Uri(url);
        var query = QueryHelpers.ParseQuery(uri.Query);
        Assert.Equal("idp.example.com", uri.Host);
        Assert.Equal("a", query["tenant"].ToString());
        Assert.Equal("a+b/c=", query["SAMLRequest"].ToString());
        Assert.False(query.ContainsKey("RelayState"));
    }

    private static bool AllowsUnsolicited(Action<SamlAuthenticationOptions> configure)
    {
        var options = CreateOptions();
        configure(options);
        return SamlRequestCorrelation.AllowsUnsolicitedResponses(options);
    }

    [UnitTest]
    public void AllowsUnsolicitedResponses_ByDefault_OnlyWithoutASignOnUrl()
    {
        Assert.True(AllowsUnsolicited(options => options.IdpSingleSignOnServiceUrl = null));
        Assert.True(AllowsUnsolicited(options => options.IdpSingleSignOnServiceUrl = " "));
        Assert.False(AllowsUnsolicited(options => options.IdpSingleSignOnServiceUrl = SamlSignInTestFlow.IdpSignOnUrl));
    }

    [UnitTest]
    public void AllowsUnsolicitedResponses_ExplicitTrue_AllowsWithOrWithoutASignOnUrl()
    {
        Assert.True(AllowsUnsolicited(options => options.AllowUnsolicitedResponses = true));
        Assert.True(AllowsUnsolicited(options =>
        {
            options.AllowUnsolicitedResponses = true;
            options.IdpSingleSignOnServiceUrl = null;
        }));
    }

    [UnitTest]
    public void AllowsUnsolicitedResponses_ExplicitFalse_RefusesWithOrWithoutASignOnUrl()
    {
        Assert.False(AllowsUnsolicited(options => options.AllowUnsolicitedResponses = false));
        Assert.False(AllowsUnsolicited(options =>
        {
            options.AllowUnsolicitedResponses = false;
            options.IdpSingleSignOnServiceUrl = null;
        }));
    }
}
