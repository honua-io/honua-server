// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.AuditLog.Abstractions;
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
