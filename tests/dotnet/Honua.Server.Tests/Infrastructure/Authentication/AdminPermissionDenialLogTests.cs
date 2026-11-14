// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Tests.Infrastructure.Authentication;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class AdminPermissionDenialLogTests
{
    [UnitTest]
    public async Task BearerRefusal_DoesNotReportApiKeyDenial()
    {
        var logger = new CaptureLogger();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "viewer")], "Bearer"));
        var requirement = new AdminPermissionRequirement();
        var context = new AuthorizationHandlerContext([requirement], user, httpContext);
        var handler = new AdminPermissionAuthorizationHandler(new HttpContextAccessor(), logger);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
        var message = Assert.Single(logger.Messages);
        Assert.DoesNotContain("API key", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Bearer", message, StringComparison.Ordinal);
    }

    private sealed class CaptureLogger : ILogger<AdminPermissionAuthorizationHandler>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
