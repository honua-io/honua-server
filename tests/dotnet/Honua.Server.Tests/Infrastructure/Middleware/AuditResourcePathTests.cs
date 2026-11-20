// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Middleware;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Honua.Server.Tests.Infrastructure.Middleware;

public sealed class AuditResourcePathTests
{
    [UnitTheory]
    [InlineData("/api/v1/console/share/link/{token}", "/api/v1/console/share/link/abc%2Bdef", "/api/v1/console/share/link/{token}")]
    [InlineData("/api/v1/console/content/{id}/share/link/{tokenId}", "/api/v1/console/content/7/share/link/42", "/api/v1/console/content/7/share/link/42")]
    [InlineData("/api/v1/admin/roles/{id}", "/api/v1/admin/roles/9", "/api/v1/admin/roles/9")]
    public void Resolve_RedactsOnlyCredentialRouteValues(string template, string path, string expected)
    {
        var context = BuildContext(template, path);
        var templateSegments = template.Split('/');
        var pathSegments = path.Split('/');
        for (var index = 0; index < templateSegments.Length; index++)
        {
            if (templateSegments[index].StartsWith('{'))
            {
                context.Request.RouteValues[templateSegments[index].Trim('{', '}')] = Uri.UnescapeDataString(pathSegments[index]);
            }
        }

        AuditResourcePath.Resolve(context).Should().Be(expected);
    }

    [UnitTest]
    public void Resolve_WhenCredentialValueIsMissing_RecordsTheTemplate()
    {
        var context = BuildContext("/wps/conformance/results/{token}", "/wps/conformance/results/live-value");

        AuditResourcePath.Resolve(context).Should().Be("/wps/conformance/results/{token}");
    }

    private static DefaultHttpContext BuildContext(string template, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.SetEndpoint(new RouteEndpoint(
            requestDelegate: _ => Task.CompletedTask,
            routePattern: RoutePatternFactory.Parse(template),
            order: 0,
            metadata: new EndpointMetadataCollection(),
            displayName: template));
        return context;
    }
}
