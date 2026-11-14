// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
using Microsoft.Net.Http.Headers;

namespace Honua.Server.Features.Console;

// Serves a verified static Console bundle beneath /console/ on the API origin. Same-origin hosting
// keeps the server's session cookie and the brokered OIDC callback usable without a Console server. Off
// unless Console:StaticRoot names an extracted bundle; the Launch Stack serves the bundle from its CDN.
internal static class ConsoleStaticBundle
{
    internal const string StaticRootKey = "Console:StaticRoot";

    internal static readonly PathString BasePath = new("/console");

    internal static IApplicationBuilder UseConsoleStaticBundle(this IApplicationBuilder app, IConfiguration configuration)
    {
        var root = configuration[StaticRootKey];
        if (string.IsNullOrWhiteSpace(root))
        {
            return app;
        }

        return app.UseMiddleware<ConsoleStaticBundleMiddleware>(OpenBundle(root));
    }

    // A configured but incomplete bundle is a deployment error, so startup fails instead of serving 404s.
    internal static PhysicalFileProvider OpenBundle(string root)
    {
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root))
        {
            throw new InvalidOperationException($"{StaticRootKey} must name an existing absolute directory.");
        }

        var provider = new PhysicalFileProvider(root, ExclusionFilters.Sensitive);
        foreach (var required in new[] { "index.html", "config.json" })
        {
            if (!provider.GetFileInfo(required).Exists)
            {
                provider.Dispose();
                throw new InvalidOperationException($"{StaticRootKey} does not contain the Console bundle file {required}.");
            }
        }

        return provider;
    }
}

internal sealed class ConsoleStaticBundleMiddleware(RequestDelegate next, PhysicalFileProvider files)
{
    // The bundle's <meta> policy carries every other directive; browsers ignore frame-ancestors there, so
    // the header adds it. Both policies apply, so the header must not widen what the meta policy allows.
    internal const string AppFramePolicy = "frame-ancestors 'self'";

    private static readonly PathString EmbedPath = new("/embed");

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(ConsoleStaticBundle.BasePath, StringComparison.Ordinal, out var remaining))
        {
            await next(context);
            return;
        }

        var method = context.Request.Method;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = "GET, HEAD";
            return;
        }

        if (!remaining.HasValue)
        {
            context.Response.StatusCode = StatusCodes.Status308PermanentRedirect;
            context.Response.Headers.Location = ConsoleStaticBundle.BasePath + "/" + context.Request.QueryString;
            return;
        }

        var (relative, file) = Resolve(remaining.Value!.TrimStart('/'));
        if (file is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var fileName = Path.GetFileName(relative);
        var (served, encoding) = SelectEncoding(context.Request, relative, file);
        ApplyContentHeaders(context.Response.Headers, fileName, served, encoding);
        ReplaceDocumentPolicy(context.Response.Headers);
        if (!remaining.StartsWithSegments(EmbedPath, StringComparison.Ordinal))
        {
            RestrictFramingToConsole(context.Response.Headers);
        }

        if (IsNotModified(context.Request, context.Response.Headers.ETag))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        context.Response.ContentLength = served.Length;
        if (HttpMethods.IsHead(method))
        {
            return;
        }

        await context.Response.SendFileAsync(served, context.RequestAborted);
    }

    private (string Relative, IFileInfo? File) Resolve(string relative)
    {
        if (relative.Length == 0)
        {
            return ("index.html", files.GetFileInfo("index.html"));
        }

        var file = files.GetFileInfo(relative);
        if (file.Exists && !file.IsDirectory)
        {
            return (relative, file);
        }

        // Extensionless paths are Console routes; a missing asset stays a 404 rather than HTML.
        return Path.HasExtension(relative) ? (relative, null) : ("index.html", files.GetFileInfo("index.html"));
    }

    private (IFileInfo File, string? Encoding) SelectEncoding(HttpRequest request, string relative, IFileInfo file)
    {
        var accepted = request.GetTypedHeaders().AcceptEncoding;
        foreach (var (encoding, suffix) in new[] { ("br", ".br"), ("gzip", ".gz") })
        {
            if (!accepted.Any(value => value.Value.Equals(encoding, StringComparison.OrdinalIgnoreCase) && value.Quality is not 0))
            {
                continue;
            }

            var variant = files.GetFileInfo(relative + suffix);
            if (variant.Exists && !variant.IsDirectory)
            {
                return (variant, encoding);
            }
        }

        return (file, null);
    }

    private static void ApplyContentHeaders(IHeaderDictionary headers, string fileName, IFileInfo served, string? encoding)
    {
        headers.ContentType = ContentTypes.TryGetContentType(fileName, out var contentType)
            ? contentType
            : "application/octet-stream";
        if (encoding is not null)
        {
            headers.ContentEncoding = encoding;
        }

        headers.Vary = HeaderNames.AcceptEncoding;

        // index.html and config.json select the release and its settings, so they are never cached.
        headers.CacheControl = fileName is "index.html" or "config.json" ? "no-store" : "no-cache";
        headers.ETag = $"\"{served.LastModified.UtcTicks:x}-{served.Length:x}{(encoding is null ? string.Empty : "-" + encoding)}\"";
        headers.LastModified = served.LastModified.ToString("R");
    }

    // Replace the API's document policy (no WebAssembly, no framing) with the Console's own.
    private static void ReplaceDocumentPolicy(IHeaderDictionary headers)
    {
        headers.Remove(HeaderNames.ContentSecurityPolicy);
        headers.Remove(HeaderNames.XFrameOptions);
        headers.ReferrerPolicy = "no-referrer";
    }

    // Public map embeds are framed by other sites; every other Console page is framed only by Console.
    private static void RestrictFramingToConsole(IHeaderDictionary headers)
    {
        headers.ContentSecurityPolicy = AppFramePolicy;
        headers.XFrameOptions = "SAMEORIGIN";
    }

    private static bool IsNotModified(HttpRequest request, string? etag) =>
        etag is not null
        && request.Headers.IfNoneMatch.Any(value => value is not null
            && value.Split(',').Any(candidate => string.Equals(candidate.Trim(), etag, StringComparison.Ordinal)));
}
