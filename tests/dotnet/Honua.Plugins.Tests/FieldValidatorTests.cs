// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Plugins.Abstractions;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Honua.Plugins.Tests;

public sealed class FieldValidatorTests
{
    private static PluginEditPipeline CreatePipeline(
        IEnumerable<IFieldValidator>? fieldValidators = null,
        IEnumerable<IFeatureValidator>? validators = null,
        IAuditLog? auditLog = null,
        HonuaEdition edition = HonuaEdition.Enterprise)
        => new(
            validators ?? [],
            fieldValidators ?? [],
            [],
            new TestLicenseEntitlementService(edition),
            auditLog ?? new RecordingAuditLog(),
            Options.Create(new PluginOptions { Enabled = true }),
            NullLogger<PluginEditPipeline>.Instance);

    private static EditHookContext Context(ImmutableDictionary<string, object?> attributes, int requestIndex = 0)
        => new("svc", 1, "Layer", "tester", "corr-1",
            [new EditHookFeature(EditKind.Create, requestIndex, ObjectId: null,
                Feature.Create(0, geometry: null, attributes))]);

    [Fact]
    public async Task FieldValidator_RejectsGovernedAttribute()
    {
        var pipeline = CreatePipeline(fieldValidators: [new EmailFieldValidator()]);
        var outcome = await pipeline.ValidateAndRunBeforeHooksAsync(
            Context(Attrs(("Email", "not-an-email"))), CancellationToken.None);

        outcome.HasRejections.Should().BeTrue();
        outcome.Rejections.Should().ContainSingle().Which.Message.Should().Be("Email must contain '@'");
    }

    [Fact]
    public async Task FieldValidator_PassesValidValue()
    {
        var pipeline = CreatePipeline(fieldValidators: [new EmailFieldValidator()]);
        var outcome = await pipeline.ValidateAndRunBeforeHooksAsync(
            Context(Attrs(("Email", "a@b.com"))), CancellationToken.None);

        outcome.HasRejections.Should().BeFalse();
    }

    [Fact]
    public async Task FieldValidator_SkippedWhenGovernedAttributeAbsent()
    {
        var validator = new EmailFieldValidator();
        var pipeline = CreatePipeline(fieldValidators: [validator]);
        var outcome = await pipeline.ValidateAndRunBeforeHooksAsync(
            Context(Attrs(("Name", "x"))), CancellationToken.None);

        outcome.HasRejections.Should().BeFalse();
        validator.Calls.Should().Be(0, "the validator only fires when its attribute is present");
    }

    [Fact]
    public async Task FieldValidator_MatchesAttributeCaseInsensitively()
    {
        var validator = new EmailFieldValidator();
        var pipeline = CreatePipeline(fieldValidators: [validator]);
        var outcome = await pipeline.ValidateAndRunBeforeHooksAsync(
            Context(Attrs(("email", "bad"))), CancellationToken.None);

        outcome.HasRejections.Should().BeTrue();
        validator.Calls.Should().Be(1);
    }

    [Fact]
    public async Task FeatureValidatorRejection_ShortCircuitsFieldValidators()
    {
        var featureValidator = new AlwaysRejectFeatureValidator();
        var fieldValidator = new EmailFieldValidator();
        var pipeline = CreatePipeline(
            validators: [featureValidator],
            fieldValidators: [fieldValidator]);

        var outcome = await pipeline.ValidateAndRunBeforeHooksAsync(
            Context(Attrs(("Email", "bad"))), CancellationToken.None);

        outcome.Rejections.Should().ContainSingle().Which.Message.Should().Be("feature rejected");
        fieldValidator.Calls.Should().Be(0, "the feature-level rejection short-circuits field validators");
    }

    [Fact]
    public async Task FieldValidatorRejection_EmitsAuditEvent()
    {
        var audit = new RecordingAuditLog();
        var pipeline = CreatePipeline(fieldValidators: [new EmailFieldValidator()], auditLog: audit);

        await pipeline.ValidateAndRunBeforeHooksAsync(
            Context(Attrs(("Email", "bad"))), CancellationToken.None);

        audit.Events.Should().ContainSingle().Which.Action.Should().Be("plugin.edit.reject");
    }

    [Theory]
    [InlineData(HonuaEdition.Community)]
    [InlineData(HonuaEdition.Pro)]
    public async Task FieldValidator_IsNoOp_WhenNotEntitled(HonuaEdition edition)
    {
        var pipeline = CreatePipeline(fieldValidators: [new EmailFieldValidator()], edition: edition);
        var outcome = await pipeline.ValidateAndRunBeforeHooksAsync(
            Context(Attrs(("Email", "bad"))), CancellationToken.None);

        outcome.HasRejections.Should().BeFalse();
    }

    private static ImmutableDictionary<string, object?> Attrs(params (string Key, object? Value)[] pairs)
        => ImmutableDictionary.CreateRange(pairs.Select(p => new KeyValuePair<string, object?>(p.Key, p.Value)));

    [Plugin("email-field", "1.0.0")]
    private sealed class EmailFieldValidator : IFieldValidator
    {
        public int Calls { get; private set; }

        public string FieldName => "Email";

        public ValueTask<PluginValidationResult> ValidateFieldAsync(
            object? value, Feature feature, PluginValidationContext context, CancellationToken cancellationToken)
        {
            Calls++;
            var ok = value is string s && s.Contains('@', StringComparison.Ordinal);
            return ValueTask.FromResult(ok
                ? PluginValidationResult.Success()
                : PluginValidationResult.Error("Email must contain '@'"));
        }
    }

    [Plugin("always-reject", "1.0.0")]
    private sealed class AlwaysRejectFeatureValidator : IFeatureValidator
    {
        public ValueTask<PluginValidationResult> ValidateAsync(
            Feature feature, PluginValidationContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(PluginValidationResult.Error("feature rejected"));
    }
}
