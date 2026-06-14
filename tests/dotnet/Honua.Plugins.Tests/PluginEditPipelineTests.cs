// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Plugins.Abstractions;
using Honua.Sample.UtilityValidationPlugin;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Honua.Plugins.Tests;

public sealed class PluginEditPipelineTests
{
    private static PluginEditPipeline CreatePipeline(
        HonuaEdition edition = HonuaEdition.Enterprise,
        bool enabled = true,
        IEnumerable<IFeatureValidator>? validators = null,
        IEnumerable<IEditHook>? hooks = null,
        IAuditLog? auditLog = null)
        => new(
            validators ?? [],
            hooks ?? [],
            new TestLicenseEntitlementService(edition),
            auditLog ?? new RecordingAuditLog(),
            Options.Create(new PluginOptions { Enabled = enabled }),
            NullLogger<PluginEditPipeline>.Instance);

    [Fact]
    public async Task Enterprise_RejectsHighVoltageTransformer()
    {
        var pipeline = CreatePipeline(validators: [new UtilityValidationPlugin()]);
        var context = TestData.Context(TestData.Create(TestData.Transformer(voltage: 69_000)));

        var outcome = await pipeline.ValidateAndRunBeforeHooksAsync(context, CancellationToken.None);

        outcome.HasRejections.Should().BeTrue();
        outcome.Rejections.Should().ContainSingle()
            .Which.Message.Should().Be("High voltage transformers require special approval");
    }

    [Fact]
    public async Task Enterprise_AllowsLowVoltageTransformer()
    {
        var pipeline = CreatePipeline(validators: [new UtilityValidationPlugin()]);
        var context = TestData.Context(TestData.Create(TestData.Transformer(voltage: 12_000)));

        var outcome = await pipeline.ValidateAndRunBeforeHooksAsync(context, CancellationToken.None);

        outcome.HasRejections.Should().BeFalse();
    }

    [Theory]
    [InlineData(HonuaEdition.Community)]
    [InlineData(HonuaEdition.Pro)]
    public async Task NonEnterprise_IsNoOp_EvenWithRejectingPlugin(HonuaEdition edition)
    {
        var pipeline = CreatePipeline(edition, validators: [new UtilityValidationPlugin()]);
        var context = TestData.Context(TestData.Create(TestData.Transformer(voltage: 99_000)));

        var outcome = await pipeline.ValidateAndRunBeforeHooksAsync(context, CancellationToken.None);

        outcome.HasRejections.Should().BeFalse("the plugin SDK is gated behind the Enterprise plugin.sdk entitlement");
        pipeline.HasPlugins.Should().BeTrue("plugins are still registered; entitlement gating happens at execution time");
    }

    [Fact]
    public async Task DisabledByConfig_IsNoOp()
    {
        var pipeline = CreatePipeline(enabled: false, validators: [new UtilityValidationPlugin()]);
        pipeline.HasPlugins.Should().BeFalse();

        var outcome = await pipeline.ValidateAndRunBeforeHooksAsync(
            TestData.Context(TestData.Create(TestData.Transformer(99_000))), CancellationToken.None);

        outcome.HasRejections.Should().BeFalse();
    }

    [Fact]
    public void NoPlugins_HasPluginsIsFalse()
    {
        CreatePipeline().HasPlugins.Should().BeFalse();
    }

    [Fact]
    public async Task Validators_RunInPluginIdOrder_AndShortCircuitOnFirstRejection()
    {
        var rejecting = new RejectingValidator();   // id "aaa-rejecting"
        var counting = new CountingValidator();     // id "zzz-counting"
        // Pass in reverse id order to prove the pipeline sorts deterministically.
        var pipeline = CreatePipeline(validators: [counting, rejecting]);
        var context = TestData.Context(TestData.Create(TestData.Transformer(1_000)));

        var outcome = await pipeline.ValidateAndRunBeforeHooksAsync(context, CancellationToken.None);

        outcome.Rejections.Should().ContainSingle().Which.Message.Should().Be("rejected by aaa");
        rejecting.Calls.Should().Be(1);
        counting.Calls.Should().Be(0, "the first validator rejected, short-circuiting the rest for that feature");
    }

    [Fact]
    public async Task BeforeHook_CanRejectEntireBatch()
    {
        var hook = new RecordingEditHook(rejectBatch: true);
        var pipeline = CreatePipeline(hooks: [hook]);
        var context = TestData.Context(
            TestData.Create(TestData.Transformer(1_000), requestIndex: 0),
            TestData.Create(TestData.Transformer(2_000), requestIndex: 1));

        var outcome = await pipeline.ValidateAndRunBeforeHooksAsync(context, CancellationToken.None);

        outcome.BatchRejected.Should().BeTrue();
        outcome.Rejections.Should().HaveCount(2, "a batch rejection rejects every feature in the batch");
        hook.BeforeCalls.Should().Be(1);
    }

    [Fact]
    public async Task AfterHooks_RunOnlyWhenEntitled()
    {
        var enterpriseHook = new RecordingEditHook();
        var enterprise = CreatePipeline(hooks: [enterpriseHook]);
        await enterprise.RunAfterHooksAsync(TestData.Context(TestData.Create(TestData.Transformer(1))), CancellationToken.None);
        enterpriseHook.AfterCalls.Should().Be(1);

        var communityHook = new RecordingEditHook();
        var community = CreatePipeline(HonuaEdition.Community, hooks: [communityHook]);
        await community.RunAfterHooksAsync(TestData.Context(TestData.Create(TestData.Transformer(1))), CancellationToken.None);
        communityHook.AfterCalls.Should().Be(0);
    }

    [Fact]
    public async Task Rejection_EmitsAuditEvent()
    {
        var audit = new RecordingAuditLog();
        var pipeline = CreatePipeline(validators: [new UtilityValidationPlugin()], auditLog: audit);

        await pipeline.ValidateAndRunBeforeHooksAsync(
            TestData.Context(TestData.Create(TestData.Transformer(99_000))), CancellationToken.None);

        audit.Events.Should().ContainSingle();
        var entry = audit.Events[0];
        entry.Action.Should().Be("plugin.edit.reject");
        entry.Outcome.Should().Be(AuditOutcome.Denied);
        entry.CorrelationId.Should().Be("corr-1");
    }
}
