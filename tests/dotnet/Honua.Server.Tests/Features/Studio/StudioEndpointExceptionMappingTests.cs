// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Server.Features.Studio;
using Honua.Server.Features.WorkflowPackages;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Studio;

public sealed class StudioEndpointExceptionMappingTests
{
    [UnitTest]
    public async Task MapStudioInvalidOperationException_UnexpectedFrameworkError_Returns500WithoutLeakingMessage()
    {
        var context = CreateContext();
        var result = StudioPackageEndpoints.MapStudioInvalidOperationException(
            context,
            NullLogger.Instance,
            "draft.create",
            "Studio package draft could not be created.",
            new InvalidOperationException("Npgsql connection pool exhausted for host=db.internal.secret"));

        await result.ExecuteAsync(context);
        var body = ReadBody(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        body.Should().NotContain("db.internal.secret");
        body.Should().NotContain("Npgsql");
        body.Should().Contain("Studio package draft could not be created.");
    }

    [UnitTest]
    public async Task MapStudioInvalidOperationException_StaleDraftGeneration_Returns409WithSafeMessage()
    {
        var context = CreateContext();
        var result = StudioPackageEndpoints.MapStudioInvalidOperationException(
            context,
            NullLogger.Instance,
            "draft.update",
            "Studio package draft could not be updated.",
            new InvalidOperationException("Stale draft generation; refresh and retry."));

        await result.ExecuteAsync(context);
        var body = ReadBody(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        body.Should().Contain("Stale draft generation; refresh and retry.");
    }

    [UnitTest]
    public async Task MapStudioArgumentException_UnexpectedArgumentException_DoesNotEchoRawMessage()
    {
        var context = CreateContext();
        var result = StudioPackageEndpoints.MapStudioArgumentException(
            context,
            NullLogger.Instance,
            "draft.create",
            new ArgumentException("Internal column secret_col failed binding"));

        await result.ExecuteAsync(context);
        var body = ReadBody(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().NotContain("secret_col");
    }

    [UnitTest]
    public async Task MapWorkflowInvalidOperationException_UnexpectedError_Returns500WithoutLeakingMessage()
    {
        var context = CreateContext();
        var result = WorkflowPackageEndpoints.MapWorkflowInvalidOperationException(
            context,
            new InvalidOperationException("Internal DI resolution failed for SecretService"));

        await result.ExecuteAsync(context);
        var body = ReadBody(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        body.Should().NotContain("SecretService");
        body.Should().Contain("The workflow package operation could not be completed.");
    }

    [UnitTest]
    public async Task MapWorkflowInvalidOperationException_DisabledPublication_Returns409WithSafeMessage()
    {
        var context = CreateContext();
        var result = WorkflowPackageEndpoints.MapWorkflowInvalidOperationException(
            context,
            new InvalidOperationException("Workflow publication 'pub-1' is disabled."));

        await result.ExecuteAsync(context);
        var body = ReadBody(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        body.Should().Contain("The workflow publication is disabled.");
    }

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
    }
}
