// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Infrastructure.Events.Outbox;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.Events.Outbox;

[Protocol(TestProtocols.TestQuality)]
public sealed class OutboxDispatcherOptionsValidatorTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Validate_DefaultOptions_Succeeds()
    {
        // The default options the runbook documents must pass validation, otherwise
        // every operator deployment would fail startup. This guards against future
        // edits to the defaults that drift below the validator's lower bounds.
        var validator = new OutboxDispatcherOptionsValidator();

        var result = validator.Validate(name: null, new OutboxDispatcherOptions());

        result.Succeeded.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Validate_NonPositiveBatchSize_Fails()
    {
        // ClaimPendingAsync short-circuits to an empty batch when BatchSize <= 0,
        // so the dispatcher would idle forever without surfacing pending rows.
        var validator = new OutboxDispatcherOptionsValidator();

        var result = validator.Validate(name: null, new OutboxDispatcherOptions { BatchSize = 0 });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("BatchSize", StringComparison.Ordinal));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Validate_NegativeBatchSize_Fails()
    {
        var validator = new OutboxDispatcherOptionsValidator();

        var result = validator.Validate(name: null, new OutboxDispatcherOptions { BatchSize = -1 });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("BatchSize", StringComparison.Ordinal));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Validate_NonPositiveIdlePollIntervalMs_Fails()
    {
        var validator = new OutboxDispatcherOptionsValidator();

        var result = validator.Validate(name: null, new OutboxDispatcherOptions { IdlePollIntervalMs = 0 });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("IdlePollIntervalMs", StringComparison.Ordinal));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Validate_NonPositiveClaimTtlSeconds_Fails()
    {
        // A zero or negative TTL would expire claims immediately, racing recovery
        // against the in-flight publish on every pass.
        var validator = new OutboxDispatcherOptionsValidator();

        var result = validator.Validate(name: null, new OutboxDispatcherOptions { ClaimTtlSeconds = 0 });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("ClaimTtlSeconds", StringComparison.Ordinal));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Validate_NonPositiveRecoveryIntervalSeconds_Fails()
    {
        var validator = new OutboxDispatcherOptionsValidator();

        var result = validator.Validate(name: null, new OutboxDispatcherOptions { RecoveryIntervalSeconds = 0 });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("RecoveryIntervalSeconds", StringComparison.Ordinal));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Validate_NonPositiveMaxRetries_Fails()
    {
        // The SQL CASE in MarkFailedAsync compares retry_count + 1 >= max_retries, so
        // a zero/negative value dead-letters every failure on first attempt.
        var validator = new OutboxDispatcherOptionsValidator();

        var result = validator.Validate(name: null, new OutboxDispatcherOptions { MaxRetries = 0 });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("MaxRetries", StringComparison.Ordinal));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Validate_NonPositiveDegradedBacklogThreshold_Fails()
    {
        // OutboxHealthCheck reports Degraded when PendingCount >= threshold; a
        // non-positive threshold flips an empty backlog to Degraded permanently.
        var validator = new OutboxDispatcherOptionsValidator();

        var result = validator.Validate(name: null, new OutboxDispatcherOptions { DegradedBacklogThreshold = 0 });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("DegradedBacklogThreshold", StringComparison.Ordinal));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Validate_NonPositiveUnhealthyDeadLetterThreshold_Fails()
    {
        // Same comparison flips a zero-dead-letter backlog to Unhealthy at threshold 0,
        // permanently failing readiness with no operator-actionable signal.
        var validator = new OutboxDispatcherOptionsValidator();

        var result = validator.Validate(name: null, new OutboxDispatcherOptions { UnhealthyDeadLetterThreshold = 0 });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("UnhealthyDeadLetterThreshold", StringComparison.Ordinal));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Validate_AllSettingsInvalid_FailsWithEverySetting()
    {
        // The validator must enumerate every misconfigured setting so operators can
        // correct one round trip rather than discovering them one at a time.
        var validator = new OutboxDispatcherOptionsValidator();

        var result = validator.Validate(name: null, new OutboxDispatcherOptions
        {
            BatchSize = 0,
            IdlePollIntervalMs = 0,
            ClaimTtlSeconds = 0,
            RecoveryIntervalSeconds = 0,
            MaxRetries = 0,
            DegradedBacklogThreshold = 0,
            UnhealthyDeadLetterThreshold = 0,
        });

        result.Failed.Should().BeTrue();
        result.Failures.Should().HaveCount(7);
    }
}
