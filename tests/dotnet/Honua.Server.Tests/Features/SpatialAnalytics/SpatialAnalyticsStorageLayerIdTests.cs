// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.SpatialAnalytics.Abstractions;
using Honua.Core.Features.SpatialAnalytics.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Protocols.SpatialAnalytics;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.SpatialAnalytics;

[Protocol(TestProtocols.SpatialAnalytics)]
public sealed class SpatialAnalyticsStorageLayerIdTests
{
    private const int PublishedTargetLayerIndex = 0;
    private const int PublishedJoinLayerIndex = 1;
    private const int TargetStorageLayerId = 42;
    private const int JoinStorageLayerId = 99;

    [UnitTest]
    [Operation(Operations.QueryClusters)]
    public async Task HandleQueryClustersPost_PassesResolvedStorageLayerIdInsteadOfLayerIndex()
    {
        var reader = Substitute.For<ISpatialAnalyticsReader>();
        reader.QueryClustersAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<ClusterQuery>(), Arg.Any<CancellationToken>())
            .Returns(ImmutableArray<IReadOnlyDictionary<string, object?>>.Empty);
        var context = await BuildJsonPostContextAsync("""{"algorithm":"dbscan","eps":500,"minPoints":2}""", reader);

        var result = await SpatialAnalyticsRequestHandlers.HandleQueryClustersPost(
            "svc-analytics", PublishedTargetLayerIndex, context);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        await reader.Received(1).QueryClustersAsync(
            TargetStorageLayerId,
            Arg.Any<FeatureQuery>(),
            Arg.Any<ClusterQuery>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.QueryClusters)]
    public async Task HandleOgcClustersPost_PassesResolvedStorageLayerIdInsteadOfLayerIndex()
    {
        var reader = Substitute.For<ISpatialAnalyticsReader>();
        reader.QueryClustersAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<ClusterQuery>(), Arg.Any<CancellationToken>())
            .Returns(ImmutableArray<IReadOnlyDictionary<string, object?>>.Empty);
        var context = await BuildJsonPostContextAsync("""{"algorithm":"dbscan","eps":500,"minPoints":2}""", reader);

        var result = await SpatialAnalyticsRequestHandlers.HandleOgcClustersPost(
            PublishedTargetLayerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), context);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        await reader.Received(1).QueryClustersAsync(
            TargetStorageLayerId,
            Arg.Any<FeatureQuery>(),
            Arg.Any<ClusterQuery>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.SpatialJoin)]
    public async Task HandleSpatialJoinPost_PassesResolvedStorageLayerIdsForTargetAndJoinLayers()
    {
        var reader = Substitute.For<ISpatialAnalyticsReader>();
        reader.QuerySpatialJoinAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<SpatialJoinQuery>(), Arg.Any<CancellationToken>())
            .Returns(ImmutableArray<IReadOnlyDictionary<string, object?>>.Empty);
        var context = await BuildJsonPostContextAsync($$"""{"joinLayerId":{{PublishedJoinLayerIndex}},"predicate":"intersects"}""", reader);

        var result = await SpatialAnalyticsRequestHandlers.HandleSpatialJoinPost(
            "svc-analytics", PublishedTargetLayerIndex, context);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        await reader.Received(1).QuerySpatialJoinAsync(
            TargetStorageLayerId,
            Arg.Any<FeatureQuery>(),
            Arg.Is<SpatialJoinQuery>(q => q.JoinLayerId == JoinStorageLayerId),
            Arg.Any<CancellationToken>());
    }

    private static async Task<DefaultHttpContext> BuildJsonPostContextAsync(string bodyJson, ISpatialAnalyticsReader reader)
    {
        var provider = CreateAnalyticsGraphProvider();
        var snapshot = await provider.GetCurrentAsync();
        var validator = CreateTargetLayerValidator(snapshot);

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<ILicenseStatusProvider>(new ProLicenseProvider())
            .AddSingleton<IMetadataV2GraphProvider>(provider)
            .AddSingleton(validator)
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .AddSingleton(Substitute.For<IFilterExpressionService>())
            .AddSingleton(Options.Create(new LimitsOptions()))
            .AddSingleton(reader)
            .BuildServiceProvider();

        var bytes = Encoding.UTF8.GetBytes(bodyJson);
        return new DefaultHttpContext
        {
            RequestServices = services,
            Request =
            {
                Method = HttpMethods.Post,
                ContentType = "application/json",
                Body = new MemoryStream(bytes),
                ContentLength = bytes.Length
            },
            Response = { Body = new MemoryStream() }
        };
    }

    private static TestMetadataV2GraphProvider CreateAnalyticsGraphProvider()
        => new TestMetadataV2GraphBuilder()
            .AddResource("res-target", "target", MetadataV2ObjectType.FeatureDataset,
                accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddResource("res-join", "join", MetadataV2ObjectType.FeatureDataset,
                accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddStorageBinding("bind-target", "res-target", "target", storageLayerId: TargetStorageLayerId)
            .AddStorageBinding("bind-join", "res-join", "join", storageLayerId: JoinStorageLayerId)
            .AddService("svc-analytics", "svc-analytics", protocols: ["FeatureServer", "OgcFeatures"])
            .AddPublication("pub-target", "svc-analytics", "res-target",
                layerIndex: PublishedTargetLayerIndex, isPrimary: true, storageBindingId: "bind-target")
            .AddPublication("pub-join", "svc-analytics", "res-join",
                layerIndex: PublishedJoinLayerIndex, isPrimary: true, storageBindingId: "bind-join")
            .BuildProvider();

    private static IResourceValidator CreateTargetLayerValidator(MetadataV2GraphSnapshot snapshot)
    {
        var targetPub = snapshot.Graph.Publications.Single(p => p.Metadata.Id == "pub-target");
        var targetRes = snapshot.Index.ResourcesById["res-target"];
        var service = snapshot.Index.ServicesById["svc-analytics"];

        var validator = Substitute.For<IResourceValidator>();
        validator.ValidateServiceLayerV2Async("svc-analytics", PublishedTargetLayerIndex, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(
                new MetadataV2ServiceLayerTriple(service, targetPub, targetRes)
                {
                    StorageLayerId = TargetStorageLayerId,
                    Snapshot = snapshot
                }));
        return validator;
    }

    private sealed class ProLicenseProvider : ILicenseStatusProvider
    {
        public LicenseStatus GetCurrentStatus()
            => new(HonuaEdition.Pro, IsValid: true, ExpiresAt: null, LicensedTo: "test");

        public Task<LicenseUploadResult> UploadLicenseAsync(
            Stream licenseStream, CancellationToken cancellationToken = default)
            => Task.FromResult(new LicenseUploadResult(false, "Stub does not support upload."));
    }
}
