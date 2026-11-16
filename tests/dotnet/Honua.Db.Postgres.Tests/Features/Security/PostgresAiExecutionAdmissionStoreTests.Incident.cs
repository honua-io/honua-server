// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Security.Admission;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Security;

public sealed partial class PostgresAiExecutionAdmissionStoreTests
{
    [IntegrationTest]
    public async Task Incident_ConfiguredWithoutTrustedEpisode_DeniesWithoutChargingAttempt()
    {
        var policy = FinancialPolicy();
        var store = Store();
        await InitializeAndResumeAsync(store, policy);
        await ExecuteAsync($$"""
            UPDATE "{{_schema}}".ai_execution_policies
            SET policy_json = jsonb_set(policy_json, '{Financial,Incident}', '{"Tokens":15,"Money":15}'::jsonb)
            WHERE deployment_id = 'deployment'
            """);

        var decision = await Store().TryAcquireAsync(FinancialContext(), Guid.NewGuid(), Frozen(policy), default);

        decision.Granted.Should().BeFalse();
        decision.Code.Should().Be("financial_incident_required");
        (await CountAttemptsAsync()).Should().Be(0);
    }

    [IntegrationTest]
    public async Task Incident_InvalidPersistedLimit_DoesNotFallBackToOtherScopes()
    {
        var policy = FinancialPolicy();
        await InitializeAndResumeAsync(Store(), policy);
        await ExecuteAsync($$"""
            UPDATE "{{_schema}}".ai_execution_policies
            SET policy_json = jsonb_set(policy_json, '{Financial,Incident}', '{"Tokens":0,"Money":15}'::jsonb)
            WHERE deployment_id = 'deployment'
            """);

        var decision = await Store().TryAcquireAsync(FinancialContext(), Guid.NewGuid(), Frozen(policy), default);

        decision.Granted.Should().BeFalse();
        decision.Code.Should().Be("invalid_policy");
        (await CountAttemptsAsync()).Should().Be(0);
    }

    [IntegrationTest]
    public async Task Incident_TwoPoolsRaceDifferentActors_OnlyOneReservationFits()
    {
        var (policy, _) = await InitializeIncidentAsync();
        var stores = new[] { Store(), Store() };
        var decisions = await Task.WhenAll(Enumerable.Range(0, 12).Select(index =>
            stores[index % 2].TryAcquireAsync(FinancialContext($"actor-{index}"), Guid.NewGuid(), Frozen(policy), default)));

        decisions.Count(result => result.Granted).Should().Be(1);
        decisions.Where(result => !result.Granted).Should().OnlyContain(result => result.Code == "financial_incident_exceeded");
        (await CountAttemptsAsync()).Should().Be(1);
    }

    [IntegrationTheory]
    [InlineData("state = 1, closed_at = clock_timestamp(), lifecycle_version = 2")]
    [InlineData("observed_at = clock_timestamp() - interval '6 minutes', started_at = clock_timestamp() - interval '7 minutes'")]
    [InlineData("lifecycle_version = 2")]
    public async Task Incident_EpisodeChangesAfterReservation_PreSendDenies(string change)
    {
        var (policy, episode) = await InitializeIncidentAsync();
        var store = Store();
        var decision = await store.TryAcquireAsync(FinancialContext(), Guid.NewGuid(), Frozen(policy), default);
        decision.Granted.Should().BeTrue();
        await ExecuteAsync($"UPDATE \"{_schema}\".ops_incident_episodes SET {change} WHERE episode_id = '{episode}'");

        (await Store().CommitFinancialSendAsync(decision.Reservation!, default)).Should().BeFalse();
        (await CountAttemptsAsync()).Should().Be(1, "pre-send denial retains the original debt");
    }

    [IntegrationTest]
    public async Task Incident_UnboundWorkflowCannotBorrowAnotherJobsEpisode()
    {
        var (policy, _) = await InitializeIncidentAsync();
        var decision = await Store().TryAcquireAsync(FinancialContext(workflow: "job:unbound"), Guid.NewGuid(), Frozen(policy), default);

        decision.Code.Should().Be("financial_incident_required");
        (await CountAttemptsAsync()).Should().Be(0);
    }

    [IntegrationTest]
    public async Task Incident_PriorPeriodSettledDebt_DoesNotResetAllowance()
    {
        var (policy, episode) = await InitializeIncidentAsync();
        var store = Store();
        var grant = (await store.TryAcquireAsync(FinancialContext(), Guid.NewGuid(), Frozen(policy), default)).Reservation!;
        (await store.CommitFinancialSendAsync(grant, default)).Should().BeTrue();
        (await store.SettleFinancialAsync(grant,
            new AiFinancialUsage(grant.Financial!.RequestDigest, "fixture-model", "incident-response", 10, 5), default))
            .Should().Be("settled");
        await store.ReleaseAsync(grant, default);
        await AssertPriorPeriodDebtAsync(policy, episode);
    }

    [IntegrationTest]
    public async Task Incident_PriorPeriodUnsettledDebt_DoesNotResetAllowance()
    {
        var (policy, episode) = await InitializeIncidentAsync();
        var store = Store();
        var grant = (await store.TryAcquireAsync(FinancialContext(), Guid.NewGuid(), Frozen(policy), default)).Reservation!;
        await store.ReleaseAsync(grant, default);
        await AssertPriorPeriodDebtAsync(policy, episode);
    }

    private async Task AssertPriorPeriodDebtAsync(AiAdmissionPolicy policy, Guid episode)
    {
        await ExecuteAsync($"UPDATE \"{_schema}\".ai_financial_invocations SET period_start = period_start - interval '1 day', reserved_at = reserved_at - interval '1 day'");
        await ExecuteAsync($"UPDATE \"{_schema}\".ops_incident_episodes SET observed_at = clock_timestamp() WHERE episode_id = '{episode}'");

        (await Store().TryAcquireAsync(FinancialContext("another-agent"), Guid.NewGuid(), Frozen(policy), default))
            .Code.Should().Be("financial_incident_exceeded");
        (await CountAttemptsAsync()).Should().Be(1);
    }

    [IntegrationTheory]
    [InlineData("charged_tokens")]
    [InlineData("charged_money")]
    public async Task Incident_EitherDimension_NotifiesAtEightyPercent(string dimension)
    {
        var (policy, _) = await InitializeIncidentAsync(new() { Tokens = 20, Money = 20 });
        (await Store().TryAcquireAsync(FinancialContext(), Guid.NewGuid(), Frozen(policy), default)).Granted.Should().BeTrue();
        var outbox = Store();
        (await outbox.ListPendingIncidentAsync(100, default)).Should().BeEmpty();
        await ExecuteAsync($"UPDATE \"{_schema}\".ai_financial_invocations SET {dimension} = 16");
        (await outbox.ListPendingIncidentAsync(100, default)).Should().ContainSingle();
    }

    [IntegrationTest]
    public async Task Incident_ThresholdNotice_SurvivesReplayAndQueuesOnlyOnce()
    {
        var (policy, episode) = await InitializeIncidentAsync();
        var grant = await Store().TryAcquireAsync(FinancialContext(), Guid.NewGuid(), Frozen(policy), default);
        grant.Granted.Should().BeTrue();
        await ApplyIncidentMigrationAsync();
        var outbox = Store();
        var notice = (await outbox.ListPendingIncidentAsync(100, default)).Should().ContainSingle().Subject;
        notice.EpisodeId.Should().Be(episode);
        notice.ChargedTokens.Should().Be(15);
        (await outbox.MarkQueuedAsync(notice, default)).Should().BeTrue();
        (await Store().MarkQueuedAsync(notice, default)).Should().BeFalse();
        await ExecuteAsync($"UPDATE \"{_schema}\".ai_financial_invocations SET charged_tokens = charged_tokens");
        (await outbox.ListPendingIncidentAsync(100, default)).Should().BeEmpty();
    }

    [IntegrationTheory]
    [InlineData("policy_json #- '{Financial,Incident}'")]
    [InlineData("jsonb_set(policy_json, '{Financial,Incident}', '{\"Tokens\":30,\"Money\":30}'::jsonb)")]
    [InlineData("jsonb_set(policy_json, '{Financial,IncidentWorkflows}', '[]'::jsonb)")]
    public async Task Incident_PolicyChangeWhileRunning_IsRejected(string replacement)
    {
        await InitializeIncidentAsync();
        var change = async () => await ExecuteAsync($"UPDATE \"{_schema}\".ai_execution_policies SET epoch = epoch + 1, policy_json = {replacement}");
        (await change.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }

    [IntegrationTheory]
    [InlineData(14, 1000)]
    [InlineData(1000, 14)]
    public async Task Incident_EitherCapIndependently_DeniesBeforeCharging(long tokens, long money)
    {
        var (policy, _) = await InitializeIncidentAsync(new() { Tokens = tokens, Money = money });
        (await Store().TryAcquireAsync(FinancialContext(), Guid.NewGuid(), Frozen(policy), default))
            .Code.Should().Be("financial_incident_exceeded");
        (await CountAttemptsAsync()).Should().Be(0);
    }

    [IntegrationTest]
    public async Task Incident_RebindingSameEpisode_PreservesAllowance()
    {
        var (policy, episode) = await InitializeIncidentAsync();
        var first = await ReserveAndReleaseAsync(policy);
        var replacement = await RebindIncidentAsync(policy, episode);

        var decision = await Store().TryAcquireAsync(FinancialContext("different-agent"), Guid.NewGuid(), Frozen(replacement), default);

        decision.Granted.Should().BeFalse();
        decision.Code.Should().Be("financial_incident_exceeded");
        (await FinancialChargeAsync(first.AttemptId)).Should().Be((15L, 15L, "reserved"));
        (await CountAttemptsAsync()).Should().Be(1);
    }

    [IntegrationTest]
    public async Task Incident_RebindingNewEpisode_GetsNewAllowance()
    {
        var (policy, _) = await InitializeIncidentAsync();
        var first = await ReserveAndReleaseAsync(policy);
        var replacement = await RebindIncidentAsync(policy, Guid.NewGuid());

        var decision = await Store().TryAcquireAsync(FinancialContext("different-agent"), Guid.NewGuid(), Frozen(replacement), default);

        decision.Granted.Should().BeTrue();
        (await FinancialChargeAsync(first.AttemptId)).Should().Be((15L, 15L, "reserved"), "rebinding must preserve deployment and actor debt");
        (await CountAttemptsAsync()).Should().Be(2);
    }

    private async Task<AiAdmissionReservation> ReserveAndReleaseAsync(AiAdmissionPolicy policy)
    {
        var store = Store();
        var reservation = (await store.TryAcquireAsync(FinancialContext(), Guid.NewGuid(), Frozen(policy), default)).Reservation!;
        await store.ReleaseAsync(reservation, default);
        return reservation;
    }

    [IntegrationTest]
    public Task Incident_LegacyReservationWriter_CannotOmitBinding() => AssertLegacyWriterRejectedAsync(
        ", 'incident_authority', NULL, 'incident_episode', NULL, 'incident_version', NULL",
        "Trusted incident workflow binding is required");

    [IntegrationTest]
    public Task Incident_LegacyReservationWriter_CannotExceedCap() => AssertLegacyWriterRejectedAsync("", "Incident spend cap reached");

    private async Task AssertLegacyWriterRejectedAsync(string identity, string expectedMessage)
    {
        var (policy, _) = await InitializeIncidentAsync();
        (await Store().TryAcquireAsync(FinancialContext(), Guid.NewGuid(), Frozen(policy), default)).Granted.Should().BeTrue();
        var write = async () => await ExecuteAsync($$"""
            INSERT INTO "{{_schema}}".ai_financial_invocations
            SELECT (jsonb_populate_record(NULL::"{{_schema}}".ai_financial_invocations,
                to_jsonb(f) || jsonb_build_object('attempt_id', '{{Guid.NewGuid():D}}'{{identity}}))).*
            FROM "{{_schema}}".ai_financial_invocations f
            """);

        var failure = (await write.Should().ThrowAsync<PostgresException>()).Which;

        failure.SqlState.Should().Be("23514");
        failure.MessageText.Should().Be(expectedMessage);
        (await CountAttemptsAsync()).Should().Be(1);
    }

    private async Task<AiAdmissionPolicy> RebindIncidentAsync(AiAdmissionPolicy policy, Guid episode)
    {
        await ExecuteAsync($$"""
            UPDATE "{{_schema}}".ops_incident_episodes SET state = 1, closed_at = clock_timestamp();
            INSERT INTO "{{_schema}}".ops_incident_episodes
                (authority_scope, episode_id, finding_id, lifecycle_version, state, started_at, observed_at)
            VALUES ('ops:deployment', '{{episode}}', 'finding', 2, 0, clock_timestamp(), clock_timestamp())
            ON CONFLICT (authority_scope, episode_id) DO UPDATE
                SET state = 0, closed_at = NULL, lifecycle_version = 2, observed_at = clock_timestamp()
            """);
        var replacement = policy with
        {
            Financial = policy.Financial! with
            {
                IncidentWorkflows = [new() { Workflow = "job:trusted", AuthorityScope = "ops:deployment", EpisodeId = episode, LifecycleVersion = 2 }]
            }
        };
        var store = Store();
        var state = await store.GetStateAsync("deployment", default);
        var stopped = await store.ApplyControlAsync(Command(AiAdmissionControlAction.Stop, state!), default);
        stopped.Applied.Should().BeTrue();
        var updated = await store.ApplyControlAsync(Command(AiAdmissionControlAction.UpdatePolicy, stopped.State!, replacement), default);
        updated.Applied.Should().BeTrue();
        (await store.ApplyControlAsync(Command(AiAdmissionControlAction.Resume, updated.State!), default)).Applied.Should().BeTrue();
        return replacement;
    }

    private async Task ApplyIncidentMigrationAsync()
    {
        var migration = await File.ReadAllTextAsync(Path.Join(AppContext.BaseDirectory, "Migrations", "208_CreateAiIncidentSpend.sql"));
        await ExecuteAsync(migration.Replace("$HonuaSchema$", $"\"{_schema}\"", StringComparison.Ordinal));
    }

    private async Task<(AiAdmissionPolicy Policy, Guid Episode)> InitializeIncidentAsync(AiFinancialLimits? limits = null)
    {
        var migration = await File.ReadAllTextAsync(Path.Join(AppContext.BaseDirectory, "Migrations", "172_CreateOpsIncidentEpisodes.sql"));
        await ExecuteAsync(migration.Replace("$HonuaSchema$", $"\"{_schema}\"", StringComparison.Ordinal));
        var episode = Guid.NewGuid();
        await ExecuteAsync($$"""
            INSERT INTO "{{_schema}}".ops_incident_episodes
                (authority_scope, episode_id, finding_id, lifecycle_version, state, started_at, observed_at)
            VALUES ('ops:deployment', '{{episode}}', 'finding', 1, 0, clock_timestamp(), clock_timestamp())
            """);
        await ApplyIncidentMigrationAsync();
        var original = FinancialPolicy();
        var policy = original with
        {
            Financial = original.Financial! with
            {
                Incident = limits ?? new() { Tokens = 15, Money = 15 },
                IncidentWorkflows = [new() { Workflow = "job:trusted", AuthorityScope = "ops:deployment", EpisodeId = episode, LifecycleVersion = 1 }]
            }
        };
        await InitializeAndResumeAsync(Store(), policy);
        return (policy, episode);
    }
}
