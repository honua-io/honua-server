// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.AuditLog;
using Honua.Infrastructure.Middleware;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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

    [UnitTest]
    public void Resolve_WhenCredentialIsNotAWholeSegment_RecordsTheTemplate()
    {
        var context = BuildContext("/api/v1/attachments/downloads/{token}.png", "/api/v1/attachments/downloads/live-value.png");
        context.Request.RouteValues["token"] = "live-value";

        AuditResourcePath.Resolve(context).Should().Be("/api/v1/attachments/downloads/{token}.png");
    }

    [UnitTheory]
    [InlineData("/api/v1/attachments/downloads/signed-bearer-token", "/api/v1/attachments/downloads/[REDACTED]")]
    [InlineData("/api/v1/console/share/link/share-secret", "/api/v1/console/share/link/[REDACTED]")]
    [InlineData("/wps/conformance/results/result-secret", "/wps/conformance/results/[REDACTED]")]
    [InlineData("/api/v1/unknown/path", "/api/v1/unknown/path")]
    public void Resolve_WhenNoRouteEndpointMatched_RedactsCredentialPaths(string path, string expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        AuditResourcePath.Resolve(context).Should().Be(expected);
    }

    [UnitTest]
    public void CurrentPolicy_WhenTheConfiguredValueCannotBind_FallsBackToTheDefault()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AuditLog:Policy:RecordSuccessfulReads"] = "sometimes" })
            .Build();
        var services = new ServiceCollection();
        services.AddHonuaAuditPolicy(configuration);
        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<AuditPolicyOptions>>();

        var read = () => monitor.CurrentValue;

        read.Should().Throw<InvalidOperationException>();
        AuditInstrumentationServiceCollectionExtensions.CurrentPolicy(monitor).RecordSuccessfulReads.Should().BeFalse();
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
