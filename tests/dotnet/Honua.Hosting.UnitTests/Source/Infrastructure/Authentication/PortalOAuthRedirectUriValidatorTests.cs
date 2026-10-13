// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Unit tests for the ArcGIS OAuth2 bridge redirect_uri allow-list (#1484), the
/// open-redirect mitigation guarding <c>/sharing/rest/oauth2/authorize</c>.
/// </summary>
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class PortalOAuthRedirectUriValidatorTests
{
    [UnitTest]
    [Operation(Operations.Security)]
    public void IsAllowed_EmptyAllowList_RejectsEverything()
    {
        PortalOAuthRedirectUriValidator.IsAllowed("https://app.example.com/cb", [])
            .Should().BeFalse();
        PortalOAuthRedirectUriValidator.IsAllowed("https://app.example.com/cb", null)
            .Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Security)]
    public void IsAllowed_ExactMatch_Allows()
    {
        var list = new[] { "https://app.example.com/oauth/redirect" };
        PortalOAuthRedirectUriValidator.IsAllowed("https://app.example.com/oauth/redirect", list)
            .Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Security)]
    public void IsAllowed_DifferentPathUnderExactEntry_Rejects()
    {
        var list = new[] { "https://app.example.com/oauth/redirect" };
        PortalOAuthRedirectUriValidator.IsAllowed("https://app.example.com/oauth/evil", list)
            .Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Security)]
    public void IsAllowed_OriginEntry_AllowsAnyPathOnSameOrigin()
    {
        var list = new[] { "https://arcgis.example.com/" };
        PortalOAuthRedirectUriValidator.IsAllowed("https://arcgis.example.com/oauth/redirect", list)
            .Should().BeTrue();
        PortalOAuthRedirectUriValidator.IsAllowed("https://arcgis.example.com/anything?x=1", list)
            .Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Security)]
    public void IsAllowed_OriginEntry_RejectsDifferentHostSchemeOrPort()
    {
        var list = new[] { "https://arcgis.example.com/" };
        PortalOAuthRedirectUriValidator.IsAllowed("https://evil.example.com/redirect", list)
            .Should().BeFalse();
        PortalOAuthRedirectUriValidator.IsAllowed("http://arcgis.example.com/redirect", list)
            .Should().BeFalse();
        PortalOAuthRedirectUriValidator.IsAllowed("https://arcgis.example.com:8443/redirect", list)
            .Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Security)]
    public void IsAllowed_NonAbsoluteOrFragmentUri_Rejects()
    {
        var list = new[] { "https://app.example.com/" };
        PortalOAuthRedirectUriValidator.IsAllowed("/relative/path", list).Should().BeFalse();
        PortalOAuthRedirectUriValidator.IsAllowed("https://app.example.com/cb#frag", list)
            .Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Security)]
    public void IsAllowed_OobUrn_OnlyMatchesWhenListedVerbatim()
    {
        const string oob = "urn:ietf:wg:oauth:2.0:oob";
        PortalOAuthRedirectUriValidator.IsAllowed(oob, ["https://app.example.com/"])
            .Should().BeFalse();
        PortalOAuthRedirectUriValidator.IsAllowed(oob, [oob])
            .Should().BeTrue();
    }
}
