// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Plugins.Abstractions;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Honua.Plugins.Tests;

public sealed class ComputedFieldPipelineTests
{
    private static ComputedFieldPipeline CreatePipeline(
        IEnumerable<IComputedFieldProvider>? providers = null,
        HonuaEdition edition = HonuaEdition.Enterprise,
        bool enabled = true)
        => new(
            providers ?? [],
            new TestLicenseEntitlementService(edition),
            Options.Create(new PluginOptions { Enabled = enabled }),
            NullLogger<ComputedFieldPipeline>.Instance);

    private static Feature FeatureWith(params (string Key, object? Value)[] pairs)
        => Feature.Create(7, geometry: null,
            ImmutableDictionary.CreateRange(pairs.Select(p => new KeyValuePair<string, object?>(p.Key, p.Value))));

    private static ComputedFieldContext Context() => new("svc", 1, "Layer", "tester");

    [Fact]
    public async Task Projects_ComputedField_OntoFeature()
    {
        var pipeline = CreatePipeline(providers: [new AreaProvider()]);
        var result = await pipeline.ProjectAsync(FeatureWith(("Width", 4d), ("Height", 5d)), Context(), CancellationToken.None);

        result.Attributes.Should().ContainKey("Area").WhoseValue.Should().Be(20d);
        result.Attributes.Should().ContainKey("Width");
    }

    [Fact]
    public async Task NoProviders_ReturnsFeatureUnchanged()
    {
        var pipeline = CreatePipeline();
        pipeline.HasComputedFields.Should().BeFalse();

        var feature = FeatureWith(("X", 1d));
        var result = await pipeline.ProjectAsync(feature, Context(), CancellationToken.None);
        result.Should().Be(feature);
    }

    [Theory]
    [InlineData(HonuaEdition.Community)]
    [InlineData(HonuaEdition.Pro)]
    public async Task NotEntitled_ReturnsFeatureUnchanged(HonuaEdition edition)
    {
        var pipeline = CreatePipeline(providers: [new AreaProvider()], edition: edition);
        var feature = FeatureWith(("Width", 4d), ("Height", 5d));
        var result = await pipeline.ProjectAsync(feature, Context(), CancellationToken.None);

        result.Attributes.Should().NotContainKey("Area");
    }

    [Fact]
    public async Task DisabledByConfig_ReturnsFeatureUnchanged()
    {
        var pipeline = CreatePipeline(providers: [new AreaProvider()], enabled: false);
        pipeline.HasComputedFields.Should().BeFalse();
        var feature = FeatureWith(("Width", 4d), ("Height", 5d));
        (await pipeline.ProjectAsync(feature, Context(), CancellationToken.None)).Attributes
            .Should().NotContainKey("Area");
    }

    [Fact]
    public async Task FaultingProvider_IsIsolated_OtherFieldsStillProjected()
    {
        var pipeline = CreatePipeline(providers: [new ThrowingProvider(), new AreaProvider()]);
        var result = await pipeline.ProjectAsync(FeatureWith(("Width", 2d), ("Height", 3d)), Context(), CancellationToken.None);

        result.Attributes.Should().NotContainKey("Boom", "a faulting provider's field is omitted");
        result.Attributes.Should().ContainKey("Area").WhoseValue.Should().Be(6d);
    }

    [Fact]
    public async Task Providers_RunInPluginIdOrder_LastWinsForSameField()
    {
        // "aaa-first" sets Tag=first; "zzz-second" sets Tag=second; ordered by id, second wins.
        var pipeline = CreatePipeline(providers: [new SecondTagProvider(), new FirstTagProvider()]);
        var result = await pipeline.ProjectAsync(FeatureWith(), Context(), CancellationToken.None);

        result.Attributes["Tag"].Should().Be("second");
    }

    [Plugin("area", "1.0.0")]
    private sealed class AreaProvider : IComputedFieldProvider
    {
        public string FieldName => "Area";

        public ValueTask<object?> ComputeAsync(Feature feature, ComputedFieldContext context, CancellationToken cancellationToken)
        {
            var w = Convert.ToDouble(feature.Attributes["Width"], CultureInfo.InvariantCulture);
            var h = Convert.ToDouble(feature.Attributes["Height"], CultureInfo.InvariantCulture);
            return ValueTask.FromResult<object?>(w * h);
        }
    }

    [Plugin("boom", "1.0.0")]
    private sealed class ThrowingProvider : IComputedFieldProvider
    {
        public string FieldName => "Boom";

        public ValueTask<object?> ComputeAsync(Feature feature, ComputedFieldContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("compute failed");
    }

    [Plugin("aaa-first", "1.0.0")]
    private sealed class FirstTagProvider : IComputedFieldProvider
    {
        public string FieldName => "Tag";

        public ValueTask<object?> ComputeAsync(Feature feature, ComputedFieldContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult<object?>("first");
    }

    [Plugin("zzz-second", "1.0.0")]
    private sealed class SecondTagProvider : IComputedFieldProvider
    {
        public string FieldName => "Tag";

        public ValueTask<object?> ComputeAsync(Feature feature, ComputedFieldContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult<object?>("second");
    }
}
