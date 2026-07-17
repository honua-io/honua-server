// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Plugins.Abstractions;

namespace Honua.Plugins.Tests;

/// <summary>Shared fakes and builders for the plugin SDK tests.</summary>
internal static class TestData
{
    public static Feature Transformer(double voltage, long id = 0)
        => Feature.Create(id, geometry: null, ImmutableDictionary.CreateRange(new Dictionary<string, object?>
        {
            ["AssetType"] = "Transformer",
            ["Voltage"] = voltage,
        }));

    public static EditHookContext Context(params EditHookFeature[] features)
        => new("test-service", 1, "Test Layer", "tester", "corr-1", [.. features]);

    public static EditHookFeature Create(Feature feature, int requestIndex = 0)
        => new(EditKind.Create, requestIndex, ObjectId: null, feature);
}

/// <summary>Records audit events for assertions.</summary>
internal sealed class RecordingAuditLog : IAuditLog
{
    public List<AuditEvent> Events { get; } = [];

    public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        Events.Add(auditEvent);
        return Task.CompletedTask;
    }
}

/// <summary>Validator that always rejects and counts invocations. Ordered first by id.</summary>
[Plugin("aaa-rejecting", "1.0.0")]
internal sealed class RejectingValidator : IFeatureValidator
{
    public int Calls { get; private set; }

    public ValueTask<PluginValidationResult> ValidateAsync(
        Feature feature, PluginValidationContext context, CancellationToken cancellationToken)
    {
        Calls++;
        return ValueTask.FromResult(PluginValidationResult.Error("rejected by aaa"));
    }
}

/// <summary>Validator that always succeeds and counts invocations. Ordered second by id.</summary>
[Plugin("zzz-counting", "1.0.0")]
internal sealed class CountingValidator : IFeatureValidator
{
    public int Calls { get; private set; }

    public ValueTask<PluginValidationResult> ValidateAsync(
        Feature feature, PluginValidationContext context, CancellationToken cancellationToken)
    {
        Calls++;
        return ValueTask.FromResult(PluginValidationResult.Success());
    }
}

/// <summary>After-hook that throws on every after-edit invocation, counting attempts.</summary>
[Plugin("faulting-after-hook", "1.0.0")]
internal sealed class FaultingAfterHook : IEditHook
{
    public int AfterAttempts { get; private set; }

    public ValueTask<EditHookResult> OnBeforeEditAsync(EditHookContext context, CancellationToken cancellationToken)
        => ValueTask.FromResult(EditHookResult.Continue());

    public ValueTask OnAfterEditAsync(EditHookContext context, CancellationToken cancellationToken)
    {
        AfterAttempts++;
        throw new InvalidOperationException("after-hook boom");
    }
}

/// <summary>
/// Minimal read-only <see cref="IFeatureReader"/> stub shared by the data-store test doubles. It
/// answers reads with empties and never throws; the data-store tests assert registration/resolution
/// through the provider registry, not query execution.
/// </summary>
internal sealed class StubFeatureReader : IFeatureReader
{
    public static StubFeatureReader Instance { get; } = new();

    public Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
        => Task.FromResult<Feature?>(null);

    public Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        => Task.FromResult(QueryResult<Feature>.Empty());

    public Task<byte[]?> QueryFlatGeobufAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        => Task.FromResult<byte[]?>(null);

    public Task<ImmutableArray<long>> QueryObjectIdsAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        => Task.FromResult(ImmutableArray<long>.Empty);

    public Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        => Task.FromResult(0L);

    public Task<FeatureExtent?> GetExtentAsync(int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default)
        => Task.FromResult<FeatureExtent?>(null);

    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryStatisticsAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        => Task.FromResult(ImmutableArray<IReadOnlyDictionary<string, object?>>.Empty);

    public Task<TemporalExtentResult?> GetTemporalExtentAsync(int layerId, string fieldName, TemporalPropertyType propertyType, CancellationToken cancellationToken = default)
        => Task.FromResult<TemporalExtentResult?>(null);

    public Task<EstimateResult> GetEstimatesAsync(int layerId, CancellationToken cancellationToken = default)
        => Task.FromResult(default(EstimateResult));

    public Task<QueryResult<Feature>> QueryTopFeaturesAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        => Task.FromResult(QueryResult<Feature>.Empty());

    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryDateBinsAsync(int layerId, FeatureQuery query, DateBinDefinition dateBin, CancellationToken cancellationToken = default)
        => Task.FromResult(ImmutableArray<IReadOnlyDictionary<string, object?>>.Empty);

    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryBinsAsync(int layerId, FeatureQuery query, BinDefinition binDefinition, CancellationToken cancellationToken = default)
        => Task.FromResult(ImmutableArray<IReadOnlyDictionary<string, object?>>.Empty);

    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryH3Async(int layerId, FeatureQuery query, H3AggregationQuery h3Query, CancellationToken cancellationToken = default)
        => Task.FromResult(ImmutableArray<IReadOnlyDictionary<string, object?>>.Empty);
}

/// <summary>
/// Read-only vector data-store plugin (issue #2856): a <c>[Plugin]</c> implementing the Core
/// <see cref="IFeatureDataProvider"/> seam, declaring the <see cref="PluginCapability.DataStore"/>
/// capability. Proves a third party can contribute a read-only source that the existing provider
/// registry/router resolve by name.
/// </summary>
[Plugin("sample-vector-source", "1.0.0",
    Description = "Read-only in-memory vector source.",
    Capabilities = PluginCapability.DataStore)]
internal sealed class ReadOnlyVectorSourcePlugin : IFeatureDataProvider
{
    public const string Name = "sample-vector-source";

    public string ProviderName => Name;

    public FeatureProviderCapabilities Capabilities => FeatureProviderCapabilities.ReadOnlyAnalytical;

    public IFeatureReader Reader => StubFeatureReader.Instance;

    public IFeatureWriter? Writer => null;
}

/// <summary>Data-store plugin that omits the required <see cref="PluginCapability.DataStore"/> flag.</summary>
[Plugin("uncapped-source", "1.0.0")]
internal sealed class UncappedDataStorePlugin : IFeatureDataProvider
{
    public string ProviderName => "uncapped-source";

    public FeatureProviderCapabilities Capabilities => FeatureProviderCapabilities.ReadOnlyAnalytical;

    public IFeatureReader Reader => StubFeatureReader.Instance;

    public IFeatureWriter? Writer => null;
}

/// <summary>Output-format plugin that omits the required <see cref="PluginCapability.OutputFormats"/> flag.</summary>
[Plugin("uncapped-format", "1.0.0")]
internal sealed class UncappedOutputFormatPlugin : IFeatureOutputFormat
{
    public string FormatId => "uncapped";

    public string MediaType => "text/plain";

    public string FileExtension => "txt";

    public ValueTask<long> WriteAsync(
        IAsyncEnumerable<Feature> features, FeatureOutputFormatContext context, Stream output, CancellationToken cancellationToken)
        => ValueTask.FromResult(0L);
}

/// <summary>Output-format plugin that illegally claims a reserved built-in wire token (<c>csv</c>).</summary>
[Plugin("reserved-format-collision", "1.0.0", Capabilities = PluginCapability.OutputFormats)]
internal sealed class ReservedTokenOutputFormatPlugin : IFeatureOutputFormat
{
    public string FormatId => "csv";

    public string MediaType => "text/csv";

    public string FileExtension => "csv";

    public ValueTask<long> WriteAsync(
        IAsyncEnumerable<Feature> features, FeatureOutputFormatContext context, Stream output, CancellationToken cancellationToken)
        => ValueTask.FromResult(0L);
}

/// <summary>Edit hook that records before/after invocations and can reject the batch.</summary>
[Plugin("hook", "1.0.0")]
internal sealed class RecordingEditHook(bool rejectBatch = false) : IEditHook
{
    public int BeforeCalls { get; private set; }

    public int AfterCalls { get; private set; }

    public ValueTask<EditHookResult> OnBeforeEditAsync(EditHookContext context, CancellationToken cancellationToken)
    {
        BeforeCalls++;
        return ValueTask.FromResult(rejectBatch ? EditHookResult.Reject("batch rejected") : EditHookResult.Continue());
    }

    public ValueTask OnAfterEditAsync(EditHookContext context, CancellationToken cancellationToken)
    {
        AfterCalls++;
        return ValueTask.CompletedTask;
    }
}
