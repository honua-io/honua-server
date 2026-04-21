// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.Text;

namespace Honua.Server.Tests.Features.Infrastructure.Security;

public class SecurityValidationTests
{
    private InputValidationMiddleware _middleware;
    private ILogger<InputValidationMiddleware> _logger;
    private InputValidationOptions _options;

    public SecurityValidationTests()
    {
        _logger = NSubstitute.Substitute.For<ILogger<InputValidationMiddleware>>();
        _options = new InputValidationOptions
        {
            Enabled = true,
            DetectSqlInjection = true,
            DetectXss = true,
            DetectCommandInjection = true,
            DetectPathTraversal = true,
            DetectLdapInjection = true,
            DetectNullBytes = true,
            DetectControlCharacters = true,
            MaxParameterLength = 8192
        };

        RequestDelegate next = (context) => Task.CompletedTask;
        _middleware = new InputValidationMiddleware(next, _logger, _options);
    }

    [Theory]
    [InlineData("'; DROP TABLE users; --")]
    [InlineData("' OR '1'='1")]
    [InlineData("' UNION SELECT * FROM users --")]
    [InlineData("admin'/**/or/**/1=1#")]
    public async Task InputValidation_DetectsSqlInjectionAttempts(string maliciousInput)
    {
        var context = CreateHttpContext();
        context.Request.QueryString = new QueryString($"?search={Uri.EscapeDataString(maliciousInput)}");

        var intercepted = false;
        var next = new RequestDelegate(_ =>
        {
            intercepted = true;
            return Task.CompletedTask;
        });

        var middleware = new InputValidationMiddleware(next, _logger, _options);
        await middleware.InvokeAsync(context);

        Assert.False(intercepted, "SQL injection attempt should have been blocked");
        Assert.Equal(400, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("<script>alert('XSS')</script>")]
    [InlineData("<iframe src=\"javascript:alert('XSS')\"></iframe>")]
    [InlineData("javascript:alert('XSS')")]
    [InlineData("<img src=x onerror=alert('XSS')>")]
    [InlineData("<svg onload=alert('XSS')>")]
    public async Task InputValidation_DetectsXssAttempts(string maliciousInput)
    {
        var context = CreateHttpContext();
        context.Request.QueryString = new QueryString($"?comment={Uri.EscapeDataString(maliciousInput)}");

        var intercepted = false;
        var next = new RequestDelegate(_ =>
        {
            intercepted = true;
            return Task.CompletedTask;
        });

        var middleware = new InputValidationMiddleware(next, _logger, _options);
        await middleware.InvokeAsync(context);

        Assert.False(intercepted, "XSS attempt should have been blocked");
        Assert.Equal(400, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\..\\windows\\system32\\config\\sam")]
    [InlineData("%2e%2e%2f%2e%2e%2f%2e%2e%2fetc%2fpasswd")]
    [InlineData("....//....//....//etc//passwd")]
    public async Task InputValidation_DetectsPathTraversalAttempts(string maliciousInput)
    {
        var context = CreateHttpContext();
        context.Request.QueryString = new QueryString($"?file={Uri.EscapeDataString(maliciousInput)}");

        var intercepted = false;
        var next = new RequestDelegate(_ =>
        {
            intercepted = true;
            return Task.CompletedTask;
        });

        var middleware = new InputValidationMiddleware(next, _logger, _options);
        await middleware.InvokeAsync(context);

        Assert.False(intercepted, "Path traversal attempt should have been blocked");
        Assert.Equal(400, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("; cat /etc/passwd")]
    [InlineData("| whoami")]
    [InlineData("&& ls -la")]
    [InlineData("$(cat /etc/passwd)")]
    [InlineData("`rm -rf /`")]
    public async Task InputValidation_DetectsCommandInjectionAttempts(string maliciousInput)
    {
        var context = CreateHttpContext();
        context.Request.QueryString = new QueryString($"?cmd={Uri.EscapeDataString(maliciousInput)}");

        var intercepted = false;
        var next = new RequestDelegate(_ =>
        {
            intercepted = true;
            return Task.CompletedTask;
        });

        var middleware = new InputValidationMiddleware(next, _logger, _options);
        await middleware.InvokeAsync(context);

        Assert.False(intercepted, "Command injection attempt should have been blocked");
        Assert.Equal(400, context.Response.StatusCode);
    }

    [Fact]
    public async Task InputValidation_AllowsLegitimateRequests()
    {
        var context = CreateHttpContext();
        context.Request.QueryString = new QueryString("?search=legitimate+search+term&format=json");

        var intercepted = false;
        var next = new RequestDelegate(_ =>
        {
            intercepted = true;
            return Task.CompletedTask;
        });

        var middleware = new InputValidationMiddleware(next, _logger, _options);
        await middleware.InvokeAsync(context);

        Assert.True(intercepted, "Legitimate request should have been allowed");
        Assert.NotEqual(400, context.Response.StatusCode);
    }

    [Fact]
    public async Task InputValidation_SkipsValidationForExcludedPaths()
    {
        _options.ExcludedPaths = new[] { "/upload" };

        var context = CreateHttpContext();
        context.Request.Path = "/upload";
        context.Request.QueryString = new QueryString("?file=../../../etc/passwd"); // Normally would be blocked

        var intercepted = false;
        var next = new RequestDelegate(_ =>
        {
            intercepted = true;
            return Task.CompletedTask;
        });

        var middleware = new InputValidationMiddleware(next, _logger, _options);
        await middleware.InvokeAsync(context);

        Assert.True(intercepted, "Request to excluded path should bypass validation");
    }

    [Fact]
    public async Task InputValidation_RespectsParameterLengthLimit()
    {
        var longString = new string('a', _options.MaxParameterLength + 1);
        var context = CreateHttpContext();
        context.Request.QueryString = new QueryString($"?data={longString}");

        var intercepted = false;
        var next = new RequestDelegate(_ =>
        {
            intercepted = true;
            return Task.CompletedTask;
        });

        var middleware = new InputValidationMiddleware(next, _logger, _options);
        await middleware.InvokeAsync(context);

        Assert.False(intercepted, "Long parameter should have been blocked");
        Assert.Equal(400, context.Response.StatusCode);
    }

    [Fact]
    public async Task InputValidation_DetectsNullByteAttempts()
    {
        var context = CreateHttpContext();
        var maliciousInput = "normal_text\0hidden_content";
        context.Request.QueryString = new QueryString($"?data={Uri.EscapeDataString(maliciousInput)}");

        var intercepted = false;
        var next = new RequestDelegate(_ =>
        {
            intercepted = true;
            return Task.CompletedTask;
        });

        var middleware = new InputValidationMiddleware(next, _logger, _options);
        await middleware.InvokeAsync(context);

        Assert.False(intercepted, "Null byte attempt should have been blocked");
        Assert.Equal(400, context.Response.StatusCode);
    }

    [Fact]
    public async Task InputValidation_WhenDisabled_AllowsAllRequests()
    {
        _options.Enabled = false;

        var context = CreateHttpContext();
        context.Request.QueryString = new QueryString("?sql='; DROP TABLE users; --");

        var intercepted = false;
        var next = new RequestDelegate(_ =>
        {
            intercepted = true;
            return Task.CompletedTask;
        });

        var middleware = new InputValidationMiddleware(next, _logger, _options);
        await middleware.InvokeAsync(context);

        Assert.True(intercepted, "Request should be allowed when validation is disabled");
    }

    private static HttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }
}