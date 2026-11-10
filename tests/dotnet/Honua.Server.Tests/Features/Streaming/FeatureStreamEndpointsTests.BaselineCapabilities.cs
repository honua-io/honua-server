// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using NSubstitute;

namespace Honua.Server.Tests.Features.Streaming;

public sealed partial class FeatureStreamEndpointsTests
{
    [IntegrationTheory]
    [InlineData(6, false)]
    [InlineData(5, null)]
    [InlineData(1, null)]
    [InlineData(0, null)]
    [Endpoint("GET /api/v1/streaming/features/capabilities")]
    public async Task Capabilities_BaselineSize_ReportsOnlyProvenOverLimit(long count, bool? expected)
    {
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryObjectIdsAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(0, checked((int)count)).Select(value => (long)value).ToImmutableArray());
        var fixture = BaselineCapabilityFixture(reader);
        await fixture.InitializeAsync();
        try
        {
            using var response = await fixture.CreateAdminClient().GetAsync("/api/v1/streaming/features/capabilities");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var layers = document.RootElement.GetProperty("data").GetProperty("layers");
            layers.GetArrayLength().Should().Be(1);
            layers[0].GetProperty("canSubscribe").GetBoolean().Should().BeTrue();
            var completion = layers[0].GetProperty("canCompleteBaseline");
            if (expected.HasValue)
            {
                completion.GetBoolean().Should().Be(expected.Value);
            }
            else
            {
                completion.ValueKind.Should().Be(JsonValueKind.Null,
                    "a feature count within the cap does not prove that the byte budget can complete");
            }

            await reader.DidNotReceive().QueryObjectIdsAsync(9, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
            await reader.Received(1)
                .QueryObjectIdsAsync(0, Arg.Is<FeatureQuery>(query => query.Limit == 6), Arg.Any<CancellationToken>());
            await reader.DidNotReceive().CountAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features/capabilities")]
    public async Task Capabilities_BaselineReaderUnavailable_ReportsUnknown()
    {
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryObjectIdsAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ImmutableArray<long>>(new InvalidOperationException("provider detail")));
        var fixture = BaselineCapabilityFixture(reader);
        await fixture.InitializeAsync();
        try
        {
            using var response = await fixture.CreateAdminClient().GetAsync("/api/v1/streaming/features/capabilities");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().NotContain("provider detail");
            using var document = JsonDocument.Parse(body);
            document.RootElement.GetProperty("data").GetProperty("layers")[0]
                .GetProperty("canCompleteBaseline").ValueKind.Should().Be(JsonValueKind.Null);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features/capabilities")]
    public async Task Capabilities_BaselineProbeExceedsLookupBudget_ReportsUnknown()
    {
        var reader = Substitute.For<IFeatureReader>();
        CancellationToken observedToken = default;
        reader.QueryObjectIdsAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var cancellationToken = call.ArgAt<CancellationToken>(2);
                observedToken = cancellationToken;
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return ImmutableArray<long>.Empty;
            });
        var fixture = BaselineCapabilityFixture(reader);
        await fixture.InitializeAsync();
        try
        {
            using var response = await fixture.CreateAdminClient().GetAsync("/api/v1/streaming/features/capabilities");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            document.RootElement.GetProperty("data").GetProperty("layers")[0]
                .GetProperty("canCompleteBaseline").ValueKind.Should().Be(JsonValueKind.Null);
            observedToken.CanBeCanceled.Should().BeTrue();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features/capabilities")]
    public async Task Capabilities_StreamingDisabled_DoesNotProbeBaseline()
    {
        var reader = Substitute.For<IFeatureReader>();
        var fixture = BaselineCapabilityFixture(reader)
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Community));
        await fixture.InitializeAsync();
        try
        {
            using var response = await fixture.CreateAdminClient().GetAsync("/api/v1/streaming/features/capabilities");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var layer = document.RootElement.GetProperty("data").GetProperty("layers")[0];
            layer.GetProperty("canSubscribe").GetBoolean().Should().BeFalse();
            layer.GetProperty("canCompleteBaseline").ValueKind.Should().Be(JsonValueKind.Null);
            await reader.DidNotReceive().QueryObjectIdsAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features/capabilities")]
    public async Task Capabilities_LargeFeatureCap_KeepsProbeBoundedAndUnknown()
    {
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryObjectIdsAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(0, 5001).Select(value => (long)value).ToImmutableArray());
        var fixture = BaselineCapabilityFixture(reader, int.MaxValue);
        await fixture.InitializeAsync();
        try
        {
            using var response = await fixture.CreateAdminClient().GetAsync("/api/v1/streaming/features/capabilities");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            document.RootElement.GetProperty("data").GetProperty("layers")[0]
                .GetProperty("canCompleteBaseline").ValueKind.Should().Be(JsonValueKind.Null);
            await reader.Received(1).QueryObjectIdsAsync(0,
                Arg.Is<FeatureQuery>(query => query.Limit == 5001), Arg.Any<CancellationToken>());
            await reader.DidNotReceive().CountAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static WebAppFixture BaselineCapabilityFixture(IFeatureReader reader, int maximumSnapshotFeatures = 5)
        => new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ReplaceService<IMetadataV2GraphProvider>(ServiceLevelAccessStreamLayerCatalog.BuildMetadataProvider())
            .ReplaceService(reader)
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("FeatureStreaming:MaxSnapshotFeatures", maximumSnapshotFeatures.ToString(CultureInfo.InvariantCulture));
                builder.UseSetting("FeatureStreaming:MaxSnapshotScanRows", Math.Max(20000, maximumSnapshotFeatures).ToString(CultureInfo.InvariantCulture));
            });
}
