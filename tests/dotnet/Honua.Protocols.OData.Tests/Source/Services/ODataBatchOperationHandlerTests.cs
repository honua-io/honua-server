// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.TestKit.Infrastructure;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Validation;
using Honua.Protocols.OData;
using Honua.Protocols.OData.Models;
using Honua.Protocols.OData.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.OData.Services;

[Protocol(TestProtocols.ODataV4)]
public sealed class ODataBatchOperationHandlerTests
{
    [UnitTest]
    [Operation(Operations.ODataBatch)]
    public async Task InvalidateCacheForBatchAsync_IgnoresFailedMutationResponses()
    {
        var (sut, context, outputCacheStore, _, _) = CreateSut();
        var request = new ODataBatchRequest
        {
            Requests =
            [
                new ODataBatchRequestItem
                {
                    Id = "write-1",
                    Method = "POST",
                    Url = "Features",
                    AtomicityGroup = "group-1",
                    Body = new Dictionary<string, object?>
                    {
                        ["LayerId"] = 1,
                        ["Attributes"] = new Dictionary<string, object?>
                        {
                            ["name"] = "Denied"
                        }
                    }
                }
            ]
        };
        var response = new ODataBatchResponse
        {
            Responses =
            [
                new ODataBatchResponseItem
                {
                    Id = "write-1",
                    Status = 403
                }
            ]
        };

        await InvokeInvalidateCacheAsync(sut, context, request, response);

        await outputCacheStore.DidNotReceive().EvictByTagAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.ODataBatch)]
    public async Task InvalidateCacheForBatchAsync_InvalidatesSuccessfulMutationLayer()
    {
        var (sut, context, outputCacheStore, _, _) = CreateSut();
        var request = new ODataBatchRequest
        {
            Requests =
            [
                new ODataBatchRequestItem
                {
                    Id = "write-1",
                    Method = "POST",
                    Url = "Features",
                    AtomicityGroup = "group-1",
                    Body = new Dictionary<string, object?>
                    {
                        ["LayerId"] = 1,
                        ["Attributes"] = new Dictionary<string, object?>
                        {
                            ["name"] = "Created"
                        }
                    }
                }
            ]
        };
        var response = new ODataBatchResponse
        {
            Responses =
            [
                new ODataBatchResponseItem
                {
                    Id = "write-1",
                    Status = 201
                }
            ]
        };

        await InvokeInvalidateCacheAsync(sut, context, request, response);

        await outputCacheStore.Received().EvictByTagAsync("layer:1", Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.ODataBatch)]
    public async Task InvalidateCacheForBatchAsync_InvalidatesSuccessfulNonAtomicMutationLayer()
    {
        var (sut, context, outputCacheStore, _, _) = CreateSut();
        var request = new ODataBatchRequest
        {
            Requests =
            [
                new ODataBatchRequestItem
                {
                    Id = "write-1",
                    Method = "POST",
                    Url = "Features",
                    Body = new Dictionary<string, object?>
                    {
                        ["LayerId"] = 1,
                        ["Attributes"] = new Dictionary<string, object?>
                        {
                            ["name"] = "Created"
                        }
                    }
                }
            ]
        };
        var response = new ODataBatchResponse
        {
            Responses =
            [
                new ODataBatchResponseItem
                {
                    Id = "write-1",
                    Status = 201
                }
            ]
        };

        await InvokeInvalidateCacheAsync(sut, context, request, response);

        await outputCacheStore.Received().EvictByTagAsync("layer:1", Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.ODataBatch)]
    public async Task PublishBatchFeatureEventsAsync_PublishesSuccessfulNonAtomicMutation()
    {
        var (sut, context, _, _, publisher) = CreateSut();
        var request = new ODataBatchRequest
        {
            Requests =
            [
                new ODataBatchRequestItem
                {
                    Id = "write-1",
                    Method = "POST",
                    Url = "Layers(1)/Features",
                    Body = new Dictionary<string, object?>
                    {
                        ["Attributes"] = new Dictionary<string, object?>
                        {
                            ["name"] = "Created"
                        }
                    }
                }
            ]
        };
        var response = new ODataBatchResponse
        {
            Responses =
            [
                new ODataBatchResponseItem
                {
                    Id = "write-1",
                    Status = 201,
                    Body = new Dictionary<string, object?>
                    {
                        ["ObjectId"] = 42L
                    }
                }
            ]
        };

        await InvokePublishFeatureEventsAsync(sut, context, request, response);

        await publisher.Received(1).PublishAsync(
            Arg.Is<FeatureChangeEventRequest>(eventRequest =>
                eventRequest.LayerId == 1 &&
                eventRequest.ObjectId == 42L &&
                eventRequest.Operation == "create" &&
                eventRequest.Protocol == Honua.ServiceDefaults.HonuaTelemetry.Protocols.OData),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.ODataBatch)]
    public async Task InvalidateCacheForBatchAsync_UsesMetadataFallbackWhenLayerCannotBeResolved()
    {
        var (sut, context, outputCacheStore, responseCache, _) = CreateSut();
        var request = new ODataBatchRequest
        {
            Requests =
            [
                new ODataBatchRequestItem
                {
                    Id = "write-1",
                    Method = "POST",
                    Url = "Features",
                    AtomicityGroup = "group-1",
                    Body = new Dictionary<string, object?>
                    {
                        ["Attributes"] = new Dictionary<string, object?>
                        {
                            ["name"] = "Created"
                        }
                    }
                }
            ]
        };
        var response = new ODataBatchResponse
        {
            Responses =
            [
                new ODataBatchResponseItem
                {
                    Id = "write-1",
                    Status = 201
                }
            ]
        };

        await InvokeInvalidateCacheAsync(sut, context, request, response);

        await outputCacheStore.Received().EvictByTagAsync("ogc-metadata", Arg.Any<CancellationToken>());
        await outputCacheStore.DidNotReceive().EvictByTagAsync("layer:1", Arg.Any<CancellationToken>());
        await responseCache.DidNotReceive().RemoveByPatternAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static async Task InvokeInvalidateCacheAsync(
        ODataBatchOperationHandler sut,
        HttpContext context,
        ODataBatchRequest batchRequest,
        ODataBatchResponse response)
    {
        var method = typeof(ODataBatchOperationHandler).GetMethod(
            "InvalidateCacheForBatchAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var task = (Task)method!.Invoke(sut, [context, batchRequest, response, CancellationToken.None])!;
        await task;
    }

    private static async Task InvokePublishFeatureEventsAsync(
        ODataBatchOperationHandler sut,
        HttpContext context,
        ODataBatchRequest batchRequest,
        ODataBatchResponse response)
    {
        var method = typeof(ODataBatchOperationHandler).GetMethod(
            "PublishBatchFeatureEventsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var task = (Task)method!.Invoke(sut, [context, batchRequest, response, CancellationToken.None])!;
        await task;
    }

    private static (
        ODataBatchOperationHandler Sut,
        DefaultHttpContext Context,
        IOutputCacheStore OutputCacheStore,
        IResponseCache ResponseCache,
        IFeatureChangeEventPublisher Publisher) CreateSut()
    {
        var outputCacheStore = Substitute.For<IOutputCacheStore>();
        var responseCache = Substitute.For<IResponseCache>();
        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var outputCacheInvalidationService = new OutputCacheInvalidationService(
            outputCacheStore,
            responseCache,
            null,
            scopeFactory,
            null,
            NullLogger<OutputCacheInvalidationService>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(outputCacheInvalidationService);
        services.AddSingleton<IMetadataV2GraphProvider>(
            new TestMetadataV2GraphProvider(new TestMetadataV2GraphBuilder().Build()));

        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };

        var dependencies = new ODataBatchDependencies(
            Substitute.For<IFeatureReader>(),
            Substitute.For<IFeatureWriter>(),
            Substitute.For<IGeometryService>(),
            new FeatureMutationValidator(Substitute.For<IGeometryValidator>()),
            Substitute.For<ICrsRegistry>(),
            new EditLimits(),
            new ODataValidationService(Substitute.For<ICommonQueryValidator>()),
            new ETagService(),
            new ODataEditParameterAdapter(NullLogger<ODataEditParameterAdapter>.Instance),
            new EditProcessor(NullLogger<EditProcessor>.Instance),
            new FeatureMutationEventService(publisher, outputCacheInvalidationService));

        var sut = new ODataBatchOperationHandler(dependencies, NullLogger<ODataBatchOperationHandler>.Instance);
        return (sut, context, outputCacheStore, responseCache, publisher);
    }
}
