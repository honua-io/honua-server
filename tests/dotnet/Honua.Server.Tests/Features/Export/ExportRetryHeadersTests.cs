// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Io.Export;
using Honua.Plugins.Abstractions;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Export;

public sealed class ExportRetryHeadersTests
{
    [UnitTest]
    public async Task HandleExport_WhenQueueIsFull_Returns503WithHonuaRetryableAndRetryAfterHeaders()
    {
        await using var services = CreateQueueFullServices();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();

        var result = await InvokeHandleExportAsync(context, CreateValidLayerValidator(), CreateLargeCountReader());
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers["Honua-Retryable"].ToString().Should().Be("true");
        context.Response.Headers.RetryAfter.ToString().Should().Be("5");
    }

    private static ServiceProvider CreateQueueFullServices()
    {
        var exportJobService = Substitute.For<IExportJobService>();
        exportJobService.StartAsync(Arg.Any<ExportJob>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException(ExportJobService.QueueFullMessage));

        return new ServiceCollection()
            .AddLogging()
            .AddSingleton(exportJobService)
            .AddSingleton(Substitute.For<ICloudFileStorage>())
            .AddSingleton(Options.Create(new RbacOptions()))
            .BuildServiceProvider();
    }

    private static IResourceValidator CreateValidLayerValidator()
    {
        var resource = new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Name = "parcels" },
            SchemaFields = [],
        };
        var triple = new MetadataV2ServiceLayerTriple(
            new MetadataV2Service(),
            new MetadataV2Layer(),
            resource)
        {
            StorageLayerId = 1,
        };
        var validator = Substitute.For<IResourceValidator>();
        validator.ValidateServiceLayerV2Async("test", 0, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(triple));
        return validator;
    }

    private static IFeatureReader CreateLargeCountReader()
    {
        var reader = Substitute.For<IFeatureReader>();
        reader.CountAsync(1, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(60_000L);
        return reader;
    }

    private static Task<IResult> InvokeHandleExportAsync(
        HttpContext context,
        IResourceValidator resourceValidator,
        IFeatureReader featureReader)
    {
        var method = typeof(ExportEndpoints).GetMethod(
            "HandleExport",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        return (Task<IResult>)method.Invoke(
            null,
            [
                "test", 0, "csv", null, null, null, null,
                resourceValidator,
                featureReader,
                Substitute.For<IStreamingFeatureStore>(),
                Substitute.For<ICrsRegistry>(),
                Substitute.For<IFeatureOutputFormatRegistry>(),
                context,
                CancellationToken.None,
            ])!;
    }
}
