// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.Catalog;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Catalog;

public sealed class GeoservicesSiteRootRedirectTests
{
    [UnitTheory]
    [InlineData("", "/", "/rest/services")]
    [InlineData("", "/rest", "/rest/services")]
    [InlineData("", "/arcgis", "/rest/services")]
    [InlineData("/arcgis", "", "/rest/services")]
    [InlineData("/ARCGIS", "", "/rest/services")]
    [InlineData("/arcgis", "/", "/arcgis/rest/services")]
    [InlineData("/arcgis", "/rest", "/arcgis/rest/services")]
    [InlineData("/mounted/arcgis", "", "/mounted/rest/services")]
    [InlineData("/mounted/arcgis", "/", "/mounted/arcgis/rest/services")]
    [InlineData("/mounted", "", "/mounted/rest/services")]
    [InlineData("/mounted", "/arcgis", "/mounted/rest/services")]
    public void SiteRoot_PreservesCanonicalAliasAndExternalMount(string pathBase, string path, string expectedLocation)
    {
        var context = new DefaultHttpContext();
        context.Request.PathBase = pathBase;
        context.Request.Path = path;

        var result = GeoservicesCatalogEndpoints.HandleSiteRoot(context).Should().BeOfType<RedirectHttpResult>().Subject;

        result.Url.Should().Be(expectedLocation);
        result.Permanent.Should().BeFalse();
        result.PreserveMethod.Should().BeFalse();
    }
}
