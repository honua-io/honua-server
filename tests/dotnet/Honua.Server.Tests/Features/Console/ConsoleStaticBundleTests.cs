// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.Server.Features.Console;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;

namespace Honua.Server.Tests.Features.Console;

[Trait("Category", "Unit")]
[Trait("Component", "Console")]
[Trait("Feature", "ConsoleStaticBundle")]
public sealed class ConsoleStaticBundleTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("console-bundle-");

    public ConsoleStaticBundleTests()
    {
        Write("index.html", "<html>console</html>");
        Write("config.json", "{\"apiBaseUrl\":\"/\",\"auth\":{\"mode\":\"oidc\"}}");
        Write("_framework/dotnet.wasm", "wasm");
        Write("_framework/dotnet.wasm.br", "br-wasm");
        Write("_framework/blazor.boot.dat", "dat");
        Write(".secret", "hidden");
    }

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public async Task Root_ServesIndexWithConsolePolicyInsteadOfApiPolicy()
    {
        var context = await SendAsync("/console/");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("<html>console</html>", Body(context));
        Assert.Equal("text/html", context.Response.ContentType);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Equal("frame-ancestors 'self'", context.Response.Headers.ContentSecurityPolicy.ToString());
        Assert.Equal("SAMEORIGIN", context.Response.Headers.XFrameOptions.ToString());
        Assert.Equal("no-referrer", context.Response.Headers.ReferrerPolicy.ToString());
    }

    [Fact]
    public async Task ExtensionlessConsoleRoute_FallsBackToIndex()
    {
        var context = await SendAsync("/console/operate/connections");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("<html>console</html>", Body(context));
    }

    [Theory]
    [InlineData("/console/_framework/missing.js")]
    [InlineData("/console/.secret")]
    [InlineData("/console/../appsettings.json")]
    public async Task MissingHiddenOrEscapingAsset_IsNotFoundRatherThanHtml(string path)
    {
        var context = await SendAsync(path);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal(string.Empty, Body(context));
    }

    [Fact]
    public async Task ConfigJson_IsNeverCached()
    {
        var context = await SendAsync("/console/config.json");

        Assert.Equal("application/json", context.Response.ContentType);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task PrecompressedVariant_IsServedWhenAccepted()
    {
        var context = await SendAsync("/console/_framework/dotnet.wasm", acceptEncoding: "gzip, br");

        Assert.Equal("br-wasm", Body(context));
        Assert.Equal("br", context.Response.Headers.ContentEncoding.ToString());
        Assert.Equal("application/wasm", context.Response.ContentType);
        Assert.Equal("Accept-Encoding", context.Response.Headers.Vary.ToString());
        Assert.Equal("no-cache", context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task UncompressedAsset_IsServedWhenEncodingIsNotAccepted()
    {
        var context = await SendAsync("/console/_framework/dotnet.wasm", acceptEncoding: "br;q=0");

        Assert.Equal("wasm", Body(context));
        Assert.False(context.Response.Headers.ContainsKey("Content-Encoding"));
    }

    [Fact]
    public async Task UnknownExtension_IsServedAsOctetStream()
    {
        var context = await SendAsync("/console/_framework/blazor.boot.dat");

        Assert.Equal("dat", Body(context));
        Assert.Equal("application/octet-stream", context.Response.ContentType);
    }

    [Fact]
    public async Task EmbedRoute_DropsFramingRestrictionsForThirdPartyHosts()
    {
        var context = await SendAsync("/console/embed/maps/one");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Content-Security-Policy"));
        Assert.False(context.Response.Headers.ContainsKey("X-Frame-Options"));
    }

    [Fact]
    public async Task MatchingEtag_ReturnsNotModified()
    {
        var first = await SendAsync("/console/_framework/blazor.boot.dat");
        var etag = first.Response.Headers.ETag.ToString();

        var second = await SendAsync("/console/_framework/blazor.boot.dat", ifNoneMatch: etag);

        Assert.Equal(StatusCodes.Status304NotModified, second.Response.StatusCode);
        Assert.Equal(string.Empty, Body(second));
    }

    [Fact]
    public async Task BasePathWithoutSlash_RedirectsAndKeepsQuery()
    {
        var context = await SendAsync("/console", query: "?x=1");

        Assert.Equal(StatusCodes.Status308PermanentRedirect, context.Response.StatusCode);
        Assert.Equal("/console/?x=1", context.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task Mutation_IsRejected()
    {
        var context = await SendAsync("/console/config.json", method: "POST");

        Assert.Equal(StatusCodes.Status405MethodNotAllowed, context.Response.StatusCode);
        Assert.Equal("GET, HEAD", context.Response.Headers.Allow.ToString());
    }

    [Fact]
    public async Task Head_ReturnsHeadersWithoutBody()
    {
        var context = await SendAsync("/console/config.json", method: "HEAD");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(context.Response.ContentLength > 0);
        Assert.Equal(string.Empty, Body(context));
    }

    [Theory]
    [InlineData("/consoles/x")]
    [InlineData("/api/v1/console/session")]
    [InlineData("/admin/auth/callback")]
    public async Task OtherPaths_PassThroughToTheApi(string path)
    {
        var context = await SendAsync(path);

        Assert.Equal(StatusCodes.Status418ImATeapot, context.Response.StatusCode);
    }

    [Fact]
    public void IncompleteBundle_FailsStartup()
    {
        File.Delete(Path.Combine(_root.FullName, "config.json"));

        var error = Assert.Throws<InvalidOperationException>(() => ConsoleStaticBundle.OpenBundle(_root.FullName));
        Assert.Contains("config.json", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("relative/console")]
    [InlineData("/does/not/exist/console")]
    public void MissingOrRelativeRoot_FailsStartup(string root)
    {
        Assert.Throws<InvalidOperationException>(() => ConsoleStaticBundle.OpenBundle(root));
    }

    private async Task<HttpContext> SendAsync(
        string path, string method = "GET", string? acceptEncoding = null, string? ifNoneMatch = null, string query = "")
    {
        using var files = ConsoleStaticBundle.OpenBundle(_root.FullName);
        var middleware = new ConsoleStaticBundleMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status418ImATeapot;
                return Task.CompletedTask;
            },
            files);
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query.Length == 0 ? null : query);
        if (acceptEncoding is not null)
        {
            context.Request.Headers.AcceptEncoding = acceptEncoding;
        }

        if (ifNoneMatch is not null)
        {
            context.Request.Headers.IfNoneMatch = ifNoneMatch;
        }

        // The API's default document policy is already present when this middleware runs.
        context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; frame-ancestors 'none'";
        context.Response.Headers.XFrameOptions = "DENY";
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context);
        return context;
    }

    private static string Body(HttpContext context) =>
        Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
