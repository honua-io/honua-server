// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Security;

/// <summary>
/// Client-owned OAuth2 state and PKCE values are opaque strings. A "--" inside one must not
/// be read as a SQL comment on the Sharing OAuth2 routes. The broker's generated hex IDs do
/// not constrain client state, and all three fields stay inspected on unrelated routes.
/// </summary>
public sealed class OAuth2OpaqueParameterValidationTests
{
    private const string Base64UrlWithDoubleDash = "Zm9v--YmFy_c3RhdGU.c2Vzc2lvbi--aWQ";

    [UnitTheory]
    [InlineData("/sharing/rest/oauth2/callback", "state")]
    [InlineData("/sharing/rest/oauth2/authorize", "state")]
    [InlineData("/sharing/rest/oauth2/authorize", "code_challenge")]
    [InlineData("/sharing/rest/oauth2/token", "code_verifier")]
    public async Task OAuth2Route_OpaqueParameterWithDoubleDash_IsAdmitted(string path, string name)
    {
        (await InvokeAsync(path, name)).Should().BeTrue();
    }

    [UnitTheory]
    [InlineData("state")]
    [InlineData("code_challenge")]
    [InlineData("code_verifier")]
    public async Task OtherRoute_OpaqueOAuthParameterWithDoubleDash_IsStillInspected(string name)
    {
        (await InvokeAsync("/rest/services/test/FeatureServer/1/query", name)).Should().BeFalse();
    }

    private static async Task<bool> InvokeAsync(string path, string name)
    {
        await using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        context.Request.QueryString = QueryString.Create(name, Base64UrlWithDoubleDash);
        context.Response.Body = new MemoryStream();
        var passed = false;
        var middleware = new InputValidationMiddleware(
            _ =>
            {
                passed = true;
                return Task.CompletedTask;
            },
            NullLogger<InputValidationMiddleware>.Instance,
            Options.Create(new InputValidationOptions()),
            Options.Create(new LimitsOptions()));
        await middleware.InvokeAsync(context);
        return passed;
    }
}
