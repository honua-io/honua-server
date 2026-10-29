// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using DbUp;
using FluentAssertions;
using Honua.Core.Features.Security.Admission;
using Honua.Db.Postgres.Features.Security.Admission;
using Honua.Infrastructure.Security.Admission;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Security;

public sealed partial class PostgresAiExecutionAdmissionStoreTests
{
    private static AiAdmissionPolicy FinancialPolicy() => new()
    {
        DeploymentAttempts = 100,
        ActorAttempts = 100,
        CohortAttempts = 100,
        WorkflowAttempts = 100,
        DeploymentConcurrency = 100,
        ActorConcurrency = 100,
        CohortConcurrency = 100,
        WorkflowConcurrency = 100,
        Financial = new AiFinancialPolicy
        {
            Currency = "TST",
            MoneyScale = 1000,
            PeriodAnchorUtc = DateTimeOffset.UnixEpoch,
            PeriodSeconds = 60,
            Deployment = new() { Tokens = 1000, Money = 1000 },
            Actor = new() { Tokens = 1000, Money = 1000 },
            Cohort = new() { Tokens = 1000, Money = 1000 },
            Workflow = new() { Tokens = 1000, Money = 1000 },
            JournalCapacity = 128,
            Basis = new()
            {
                Id = "synthetic-v1",
                SourceDigest = new string('a', 64),
                Provider = "fixture",
                Kind = "openai",
                BillingSchema = "openai-basic-text-v1",
                ServiceTier = "default",
                UseCase = "reporting-narrative",
                Destination = "https://fixture.invalid/v1/chat/completions",
                Model = "fixture-model",
                EffectiveUtc = DateTimeOffset.UnixEpoch,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
                MaximumRequestBytes = 1024,
                MaximumInputTokens = 10,
                MaximumOutputTokens = 5,
                InputRatePerMillion = 1_000_000,
                OutputRatePerMillion = 1_000_000
            }
        }
    };

    private static AiExecutionContext FinancialContext(string actor = "actor", string? workflow = "job:trusted")
        => AiExecutionContext.Create("deployment", actor, "reporting-narrative", workflow);

    private static AiFinancialRequest Frozen(AiAdmissionPolicy policy, byte[]? body = null)
        => new("fixture", "https://fixture.invalid/v1/chat/completions", "fixture-model", 5,
            body ?? Encoding.UTF8.GetBytes("{\"model\":\"fixture-model\",\"max_tokens\":5,\"service_tier\":\"default\",\"message\":\"FAKE_PROMPT_SECRET\"}"), policy.Financial!.Basis.Digest);

    private ServiceProvider Coordinator(PostgresAiExecutionAdmissionStore store)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKeyedScoped<IAiExecutionAdmissionStore>("postgres", (_, _) => store);
        services.AddAiExecutionAdmission(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["AiExecutionAdmission:DeploymentId"] = "deployment", ["AiExecutionAdmission:Backend"] = "postgres" }).Build());
        return services.BuildServiceProvider();
    }

    [IntegrationTheory]
    [InlineData("deployment", false)]
    [InlineData("actor", false)]
    [InlineData("cohort", false)]
    [InlineData("workflow", false)]
    [InlineData("deployment", true)]
    [InlineData("actor", true)]
    [InlineData("cohort", true)]
    [InlineData("workflow", true)]
    public async Task FinancialAcquire_TwoIndependentCoordinators_EachMoneyAndTokenScopeIsAtomic(string scope, bool money)
    {
        var policy = FinancialPolicy();
        var limit = new AiFinancialLimits { Tokens = money ? 1000 : 15, Money = money ? 15 : 1000 };
        policy = policy with
        {
            Financial = scope switch
            {
                "deployment" => policy.Financial! with { Deployment = limit },
                "actor" => policy.Financial! with { Actor = limit },
                "cohort" => policy.Financial! with { Cohort = limit },
                _ => policy.Financial! with { Workflow = limit }
            }
        };
        var first = Store(); var second = Store();
        await InitializeAndResumeAsync(first, policy);
        await using var nodeA = Coordinator(first); await using var nodeB = Coordinator(second);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 12).Select(async index =>
        {
            await start.Task;
            var context = FinancialContext(scope is "actor" or "cohort" ? "same-actor" : "actor-" + index,
                scope == "workflow" ? "job:same" : "job:" + index);
            try { return await (index % 2 == 0 ? nodeA : nodeB).GetRequiredService<IAiExecutionAdmission>().AcquireAsync(context, Frozen(policy), default); }
            catch (AiAdmissionException) { return null; }
        }).ToArray();
        start.SetResult();
        var permits = await Task.WhenAll(tasks);
        permits.Count(p => p is not null).Should().Be(1);
        (await CountAttemptsAsync()).Should().Be(1, "financial denials cannot debit an attempt or another scope");
        (await FinancialCountAsync()).Should().Be(1);
        foreach (var permit in permits.OfType<IAiExecutionPermit>()) await permit.DisposeAsync();
    }

    [IntegrationTest]
    public async Task FinancialControls_InitializeResumeAndStopWithLivePermit_PreserveDebt()
    {
        var store = Store(); var policy = FinancialPolicy();
        var state = await InitializeAndResumeAsync(store, policy);
        var grant = await store.TryAcquireAsync(FinancialContext(), Guid.NewGuid(), Frozen(policy), default);
        grant.Granted.Should().BeTrue();
        var stopped = await store.ApplyControlAsync(Command(AiAdmissionControlAction.Stop, state), default);
        stopped.Applied.Should().BeTrue("emergency stop must work with live financial attempts");
        (await store.CommitFinancialSendAsync(grant.Reservation!, default)).Should().BeFalse();
        await store.ReleaseAsync(grant.Reservation!, default);
        var resumed = await store.ApplyControlAsync(Command(AiAdmissionControlAction.Resume, stopped.State!), default);
        resumed.Applied.Should().BeTrue("a regular stopped ledger can resume without resetting liability");
        (await FinancialCountAsync()).Should().Be(1);
    }

    [IntegrationTest]
    public async Task FinancialActivation_RequiresDrainedLegacyPermitsAndFencesLegacySqlWriters()
    {
        var store = Store(); var state = await InitializeAndResumeAsync(store, new AiAdmissionPolicy());
        var old = await store.TryAcquireAsync(Context(), Guid.NewGuid(), default);
        var stopped = await store.ApplyControlAsync(Command(AiAdmissionControlAction.Stop, state), default);
        var policy = FinancialPolicy();
        (await store.ApplyControlAsync(Command(AiAdmissionControlAction.UpdatePolicy, stopped.State!, policy), default)).Code.Should().Be("policy_not_drained");
        await store.ReleaseAsync(old.Reservation!, default);
        var updated = await store.ApplyControlAsync(Command(AiAdmissionControlAction.UpdatePolicy, stopped.State!, policy), default);
        updated.Applied.Should().BeTrue();
        var resumed = await store.ApplyControlAsync(Command(AiAdmissionControlAction.Resume, updated.State!), default);
        resumed.Applied.Should().BeTrue();
        var legacyInsert = async () => await ExecuteAsync($"""
            INSERT INTO "{_schema}".ai_execution_attempts (deployment_id, attempt_id, generation_id, actor_key, use_case,
                admitted_at, deadline_at, lease_expires_at, grant_epoch)
            VALUES ('deployment', '{Guid.NewGuid():D}', '{resumed.State!.Generation:D}', repeat('a',64), 'studio-chat',
                clock_timestamp(), clock_timestamp()+INTERVAL '30 seconds', clock_timestamp()+INTERVAL '60 seconds', {resumed.State.Epoch})
            """);
        (await legacyInsert.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
        var legacyUpdate = async () => await ExecuteAsync($"""
            UPDATE "{_schema}".ai_execution_policies SET policy_json = policy_json - 'Financial' WHERE deployment_id = 'deployment'
            """);
        (await legacyUpdate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
        var legacyUpsert = async () => await ExecuteAsync($"""
            INSERT INTO "{_schema}".ai_execution_policies (deployment_id,schema_version,policy_json,generation_id,revision,epoch,stopped,not_before)
            SELECT deployment_id,schema_version,policy_json - 'Financial',generation_id,revision,epoch,stopped,not_before
            FROM "{_schema}".ai_execution_policies WHERE deployment_id='deployment'
            ON CONFLICT (deployment_id) DO UPDATE SET policy_json = EXCLUDED.policy_json
            """);
        (await legacyUpsert.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
        (await FinancialCountAsync()).Should().Be(0);
        (await CountAttemptsAsync()).Should().Be(1, "only the released pre-transition attempt exists");
    }

    [IntegrationTest]
    public async Task FinancialSend_OnlyOneDurableClaimAndImmutableRequestBasis_Authorizes()
    {
        var store = Store(); var policy = FinancialPolicy(); await InitializeAndResumeAsync(store, policy);
        var body = Frozen(policy).CopyBody(); var request = Frozen(policy, body); body[0] = (byte)'x';
        request.CopyBody()[0].Should().Be((byte)'{');
        var grant = (await store.TryAcquireAsync(FinancialContext(), Guid.NewGuid(), request, default)).Reservation!;
        var wrong = grant with { Financial = grant.Financial! with { RequestDigest = new string('b', 64) } };
        (await store.CommitFinancialSendAsync(wrong, default)).Should().BeFalse();
        var claims = await Task.WhenAll(Store().CommitFinancialSendAsync(grant, default), Store().CommitFinancialSendAsync(grant, default));
        claims.Count(x => x).Should().Be(1);
        (await FinancialCountAsync()).Should().Be(1);
        (await FinancialJsonAsync()).Should().NotContain("FAKE_PROMPT_SECRET");
        var rejected = new AiFinancialRequest(request.Provider, request.Destination, "different-model", 5, request.CopyBody(), policy.Financial!.Basis.Digest);
        (await store.TryAcquireAsync(FinancialContext("other"), Guid.NewGuid(), rejected, default)).Granted.Should().BeFalse();
    }

    [IntegrationTest]
    public async Task FinancialSettlement_UsesOriginalBasisAfterStopRevisionAndIsIdempotent()
    {
        var store = Store(); var policy = FinancialPolicy(); var state = await InitializeAndResumeAsync(store, policy);
        var grant = (await store.TryAcquireAsync(FinancialContext(), Guid.NewGuid(), Frozen(policy), default)).Reservation!;
        (await store.CommitFinancialSendAsync(grant, default)).Should().BeTrue();
        await store.ReleaseAsync(grant, default);
        var stopped = await store.ApplyControlAsync(Command(AiAdmissionControlAction.Stop, state), default);
        var replacement = policy with { Financial = policy.Financial! with { Basis = policy.Financial!.Basis with { Id = "synthetic-v2", InputRatePerMillion = 2_000_000 } } };
        (await store.ApplyControlAsync(Command(AiAdmissionControlAction.UpdatePolicy, stopped.State!, replacement), default)).Applied.Should().BeTrue();
        var receipt = new AiFinancialUsage(grant.Financial!.RequestDigest, "fixture-model", "response-original", 2, 1);
        (await Store().SettleFinancialAsync(grant, receipt, default)).Should().Be("settled");
        (await Store().SettleFinancialAsync(grant, receipt, default)).Should().Be("settled");
        (await FinancialChargeAsync(grant.AttemptId)).Should().Be((3L, 3L, "settled"));
    }

    [IntegrationTest]
    public async Task FinancialSettlement_ConflictingReusedAndOverflowReceipts_StopAndRetainDebt()
    {
        var store = Store(); var policy = FinancialPolicy(); await InitializeAndResumeAsync(store, policy);
        var first = (await store.TryAcquireAsync(FinancialContext(), Guid.NewGuid(), Frozen(policy), default)).Reservation!;
        var second = (await store.TryAcquireAsync(FinancialContext("other"), Guid.NewGuid(), Frozen(policy), default)).Reservation!;
        (await store.CommitFinancialSendAsync(first, default)).Should().BeTrue();
        (await store.CommitFinancialSendAsync(second, default)).Should().BeTrue();
        var receipt = new AiFinancialUsage(first.Financial!.RequestDigest, "fixture-model", "reused-response", 2, 1);
        var results = await Task.WhenAll(Store().SettleFinancialAsync(first, receipt, default),
            Store().SettleFinancialAsync(second, receipt with { RequestDigest = second.Financial!.RequestDigest }, default));
        results.Should().BeEquivalentTo("settled", "conflict");
        var charges = new[] { await FinancialChargeAsync(first.AttemptId), await FinancialChargeAsync(second.AttemptId) };
        charges.Should().Contain((3L, 3L, "settled")).And.Contain((15L, 15L, "conflict"));
        (await store.GetStateAsync("deployment", default))!.Stopped.Should().BeTrue();
        var overflow = receipt with { ProviderResponseId = "huge-response", InputTokens = 0, OutputTokens = 0, UnrepresentableUsageDigest = new string('d', 64) };
        (await store.SettleFinancialAsync(first, overflow, default)).Should().Be("conflict");
        (await FinancialChargeAsync(first.AttemptId)).Tokens.Should().BeGreaterThanOrEqualTo(15);
    }

    [IntegrationTest]
    public async Task FinancialSettlement_ActualOverrunRemainsChargedAndCannotResume()
    {
        var store = Store(); var policy = FinancialPolicy(); await InitializeAndResumeAsync(store, policy);
        var grant = (await store.TryAcquireAsync(FinancialContext(), Guid.NewGuid(), Frozen(policy), default)).Reservation!;
        (await store.CommitFinancialSendAsync(grant, default)).Should().BeTrue();
        (await store.SettleFinancialAsync(grant, new(grant.Financial!.RequestDigest, "fixture-model", "overrun", 20, 8), default)).Should().Be("overrun");
        (await FinancialChargeAsync(grant.AttemptId)).Should().Be((28L, 28L, "overrun"));
        var stopped = (await store.GetStateAsync("deployment", default))!;
        (await store.ApplyControlAsync(Command(AiAdmissionControlAction.Resume, stopped), default)).Code.Should().Be("financial_stop");
    }

    [IntegrationTest]
    public async Task FinancialUnknownLiability_SurvivesRolloverRestartAndAttemptDeletion()
    {
        var store = Store(); var policy = FinancialPolicy(); policy = policy with { Financial = policy.Financial! with { Deployment = new() { Tokens = 15, Money = 15 } } };
        await InitializeAndResumeAsync(store, policy);
        var grant = (await store.TryAcquireAsync(FinancialContext(), Guid.NewGuid(), Frozen(policy), default)).Reservation!;
        (await store.CommitFinancialSendAsync(grant, default)).Should().BeTrue(); await store.ReleaseAsync(grant, default);
        await ExecuteAsync($"""
            UPDATE "{_schema}".ai_financial_invocations SET period_start=period_start-INTERVAL '1 day';
            DELETE FROM "{_schema}".ai_execution_attempts;
            """);
        (await Store().TryAcquireAsync(FinancialContext("new-actor"), Guid.NewGuid(), Frozen(policy), default)).Code.Should().Be("financial_budget_exceeded");
        (await FinancialCountAsync()).Should().Be(1);
        (await Store().SettleFinancialAsync(grant, new(grant.Financial!.RequestDigest, "fixture-model", "late-original-response", 2, 1), default)).Should().Be("settled");
        (await Store().TryAcquireAsync(FinancialContext("new-actor"), Guid.NewGuid(), Frozen(policy), default)).Granted.Should().BeTrue();
        (await FinancialChargeAsync(grant.AttemptId)).Should().Be((3L, 3L, "settled"));
        (await FinancialCountAsync()).Should().Be(2, "settlement retains the original-period receipt rather than deleting history");
    }

    [IntegrationTest]
    public async Task FinancialBasis_ChangedDigestAndLegacyAcquire_DenyWithoutPartialCharges()
    {
        var store = Store(); var policy = FinancialPolicy(); await InitializeAndResumeAsync(store, policy);
        (await store.TryAcquireAsync(FinancialContext(), Guid.NewGuid(), default)).Code.Should().Be("financial_unsupported");
        var wrong = new AiFinancialRequest("fixture", "https://fixture.invalid/v1/chat/completions", "fixture-model", 5, Frozen(policy).CopyBody(), new string('b', 64));
        (await store.TryAcquireAsync(FinancialContext(), Guid.NewGuid(), wrong, default)).Granted.Should().BeFalse();
        (await CountAttemptsAsync()).Should().Be(0); (await FinancialCountAsync()).Should().Be(0);
    }

    [IntegrationTest]
    public async Task FinancialJournal_FullDeniesWithoutEvictionOrPartialAttemptCharge()
    {
        var store = Store(); var policy = FinancialPolicy();
        policy = policy with { Financial = policy.Financial! with { JournalCapacity = 1 } };
        await InitializeAndResumeAsync(store, policy);
        var grant = (await store.TryAcquireAsync(FinancialContext(), Guid.NewGuid(), Frozen(policy), default)).Reservation!;
        await store.ReleaseAsync(grant, default);
        (await Store().TryAcquireAsync(FinancialContext("other"), Guid.NewGuid(), Frozen(policy), default)).Code.Should().Be("financial_ledger_full");
        (await FinancialCountAsync()).Should().Be(1);
        (await CountAttemptsAsync()).Should().Be(1);
    }

    [IntegrationTheory]
    [InlineData("studio-chat")]
    [InlineData("imagery-inference")]
    public async Task FinancialMode_UnsupportedModelSurface_DeniesBeforeAttemptCharge(string useCase)
    {
        var store = Store(); var policy = FinancialPolicy(); await InitializeAndResumeAsync(store, policy);
        var context = AiExecutionContext.Create("deployment", "verified-actor", useCase);
        (await store.TryAcquireAsync(context, Guid.NewGuid(), default)).Code.Should().Be("financial_unsupported");
        (await FinancialCountAsync()).Should().Be(0); (await CountAttemptsAsync()).Should().Be(0);
    }

    [IntegrationTest]
    public async Task FinancialBasis_DatabaseExpiryPreventsSendButOriginalSentDebtStillSettles()
    {
        var store = Store(); var policy = FinancialPolicy();
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        {
            await using var clock = new NpgsqlCommand("SELECT clock_timestamp()", connection);
            var now = new DateTimeOffset((DateTime)(await clock.ExecuteScalarAsync())!);
            policy = policy with { Financial = policy.Financial! with { Basis = policy.Financial!.Basis with { ExpiresUtc = now.AddSeconds(5) } } };
        }
        await InitializeAndResumeAsync(store, policy);
        var sent = (await store.TryAcquireAsync(FinancialContext(), Guid.NewGuid(), Frozen(policy), default)).Reservation!;
        var unsent = (await store.TryAcquireAsync(FinancialContext("other"), Guid.NewGuid(), Frozen(policy), default)).Reservation!;
        (await store.CommitFinancialSendAsync(sent, default)).Should().BeTrue();
        await Task.Delay(TimeSpan.FromMilliseconds(5500));
        (await store.CommitFinancialSendAsync(unsent, default)).Should().BeFalse();
        (await store.SettleFinancialAsync(sent, new(sent.Financial!.RequestDigest, "fixture-model", "expired-original", 2, 1), default)).Should().Be("settled");
        (await FinancialChargeAsync(unsent.AttemptId)).Should().Be((15L, 15L, "reserved"));
        (await store.TryAcquireAsync(FinancialContext("third"), Guid.NewGuid(), Frozen(policy), default)).Code.Should().Be("financial_basis_expired");
    }

    [IntegrationTheory]
    [InlineData("currency")]
    [InlineData("scale")]
    [InlineData("anchor")]
    [InlineData("period")]
    public async Task FinancialPolicy_AccountingIdentityChanges_AreFencedInControlsAndLegacySql(string change)
    {
        var store = Store(); var policy = FinancialPolicy(); var state = await InitializeAndResumeAsync(store, policy);
        var stop = await store.ApplyControlAsync(Command(AiAdmissionControlAction.Stop, state), default);
        var altered = policy with
        {
            Financial = change switch
            {
                "currency" => policy.Financial! with { Currency = "USD" },
                "scale" => policy.Financial! with { MoneyScale = 100 },
                "anchor" => policy.Financial! with { PeriodAnchorUtc = DateTimeOffset.UnixEpoch.AddSeconds(60) },
                _ => policy.Financial! with { PeriodSeconds = 120 }
            }
        };
        (await store.ApplyControlAsync(Command(AiAdmissionControlAction.UpdatePolicy, stop.State!, altered), default)).Code.Should().Be("invalid_control");
        var oldWriter = async () =>
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"UPDATE \"{_schema}\".ai_execution_policies SET policy_json=@policy WHERE deployment_id='deployment'", connection);
            command.Parameters.AddWithValue("policy", NpgsqlTypes.NpgsqlDbType.Jsonb, System.Text.Json.JsonSerializer.Serialize(altered, AiAdmissionStoreJsonContext.Default.AiAdmissionPolicy));
            await command.ExecuteNonQueryAsync();
        };
        (await oldWriter.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
        (await store.GetStateAsync("deployment", default))!.Policy.Financial.Should().BeEquivalentTo(policy.Financial);
    }

    [IntegrationTest]
    public async Task FinancialResponseIdentity_CaseOnlyProviderUpdate_CannotSettleRepeatedResponse()
    {
        var store = Store(); var policy = FinancialPolicy(); var state = await InitializeAndResumeAsync(store, policy);
        var first = (await store.TryAcquireAsync(FinancialContext(), Guid.NewGuid(), Frozen(policy), default)).Reservation!;
        (await store.CommitFinancialSendAsync(first, default)).Should().BeTrue();
        var receipt = new AiFinancialUsage(first.Financial!.RequestDigest, "fixture-model", "same-physical-response", 2, 1);
        (await store.SettleFinancialAsync(first, receipt, default)).Should().Be("settled");
        await store.ReleaseAsync(first, default);
        var stopped = await store.ApplyControlAsync(Command(AiAdmissionControlAction.Stop, state), default);
        var replacement = policy with { Financial = policy.Financial! with { Basis = policy.Financial!.Basis with { Id = "synthetic-v2", Provider = "FIXTURE" } } };
        var updated = await store.ApplyControlAsync(Command(AiAdmissionControlAction.UpdatePolicy, stopped.State!, replacement), default);
        updated.Applied.Should().BeTrue();
        (await store.ApplyControlAsync(Command(AiAdmissionControlAction.Resume, updated.State!), default)).Applied.Should().BeTrue();
        var request = new AiFinancialRequest("FIXTURE", replacement.Financial!.Basis.Destination, "fixture-model", 5, Frozen(policy).CopyBody(), replacement.Financial.Basis.Digest);
        var second = (await store.TryAcquireAsync(FinancialContext(), Guid.NewGuid(), request, default)).Reservation!;
        (await store.CommitFinancialSendAsync(second, default)).Should().BeTrue();
        (await store.SettleFinancialAsync(second, receipt with { RequestDigest = second.Financial!.RequestDigest }, default)).Should().Be("conflict");
        (await FinancialChargeAsync(second.AttemptId)).Should().Be((15L, 15L, "conflict"));
        (await store.GetStateAsync("deployment", default))!.Stopped.Should().BeTrue();
    }

    [IntegrationTest]
    public async Task FinancialSettlement_ConflictingPartialReceipts_RetainsIndependentKnownHighWaterMarks()
    {
        var store = Store(); var policy = FinancialPolicy(); await InitializeAndResumeAsync(store, policy);
        var grant = (await store.TryAcquireAsync(FinancialContext(), Guid.NewGuid(), Frozen(policy), default)).Reservation!;
        (await store.CommitFinancialSendAsync(grant, default)).Should().BeTrue();
        var first = new AiFinancialUsage(grant.Financial!.RequestDigest, "fixture-model", "partial-response", 100, 0,
            UnsupportedUsageDigest: new string('d', 64), OutputTokensKnown: false);
        (await store.SettleFinancialAsync(grant, first, default)).Should().Be("overrun");
        (await FinancialChargeAsync(grant.AttemptId)).Should().Be((105L, 105L, "overrun"));
        var second = first with
        {
            InputTokens = 0,
            OutputTokens = 100,
            InputTokensKnown = false,
            OutputTokensKnown = true,
            UnsupportedUsageDigest = new string('e', 64)
        };
        (await store.SettleFinancialAsync(grant, second, default)).Should().Be("conflict");
        (await FinancialChargeAsync(grant.AttemptId)).Should().Be((200L, 200L, "conflict"));
    }

    [IntegrationTest]
    public async Task FinancialMigration_IndependentJournalReapplication_PreservesLiabilityPolicyAndFences()
    {
        var store = Store(); var policy = FinancialPolicy();
        var state = await InitializeAndResumeAsync(store, policy);
        var grant = (await store.TryAcquireAsync(FinancialContext(), Guid.NewGuid(), Frozen(policy), default)).Reservation!;
        (await store.CommitFinancialSendAsync(grant, default)).Should().BeTrue();
        await store.ReleaseAsync(grant, default);
        (await FinancialChargeAsync(grant.AttemptId)).Should().Be((15L, 15L, "send-committed"));
        var before = await FinancialMigrationSnapshotAsync();
        using (var snapshot = JsonDocument.Parse(before))
        {
            snapshot.RootElement.GetProperty("policies").GetArrayLength().Should().Be(1);
            snapshot.RootElement.GetProperty("liabilities").GetArrayLength().Should().Be(1);
            snapshot.RootElement.GetProperty("guards").GetArrayLength().Should().Be(2);
        }
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "CreateAiFinancialAccounting.sql"));
        // Independent fixture journals share one target schema. Each new journal must
        // execute the SQL again rather than merely skip an already-journaled script.
        for (var journal = 0; journal < 2; journal++)
        {
            var result = DeployChanges.To.PostgresqlDatabase(fixture.ConnectionString, _schema)
                .JournalToPostgresqlTable(_schema, $"financial_repeat_journal_{journal}")
                .WithScript("142_CreateAiFinancialAccounting.sql", sql)
                .WithVariable("HonuaSchema", $"\"{_schema}\"")
                .WithTransaction()
                .Build().PerformUpgrade();
            result.Successful.Should().BeTrue(result.Error?.ToString());
            (await FinancialMigrationSnapshotAsync()).Should().Be(before,
                "reapplication must preserve raw policy, debt, original basis and guard identities/privileges");
        }
        var unpaidWriter = async () => await ExecuteAsync($"""
            INSERT INTO "{_schema}".ai_execution_attempts (deployment_id, attempt_id, generation_id, actor_key, use_case,
                admitted_at, deadline_at, lease_expires_at, grant_epoch)
            VALUES ('deployment', '{Guid.NewGuid():D}', '{state.Generation:D}', repeat('a',64), 'studio-chat',
                clock_timestamp(), clock_timestamp()+INTERVAL '30 seconds', clock_timestamp()+INTERVAL '60 seconds', {state.Epoch})
            """);
        var unpaid = (await unpaidWriter.Should().ThrowAsync<PostgresException>()).Which;
        unpaid.SqlState.Should().Be("23514");
        unpaid.MessageText.Should().Be("Financial reservation is required before attempt admission");
        var downgrade = async () => await ExecuteAsync($"""
            UPDATE "{_schema}".ai_execution_policies SET policy_json = policy_json - 'Financial' WHERE deployment_id = 'deployment'
            """);
        var refused = (await downgrade.Should().ThrowAsync<PostgresException>()).Which;
        refused.SqlState.Should().Be("23514");
        refused.MessageText.Should().Be("Financial policy downgrade is forbidden");
        (await FinancialMigrationSnapshotAsync()).Should().Be(before);
        (await CountAttemptsAsync()).Should().Be(1, "the unpaid writer must not add an attempt");
    }

    private async Task<string> FinancialMigrationSnapshotAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var query = new NpgsqlCommand($"""
            SELECT jsonb_build_object(
                'policies', (SELECT jsonb_agg(to_jsonb(p) ORDER BY deployment_id) FROM "{_schema}".ai_execution_policies p),
                'liabilities', (SELECT jsonb_agg(to_jsonb(f) ORDER BY attempt_id) FROM "{_schema}".ai_financial_invocations f),
                'guards', (SELECT jsonb_agg(jsonb_build_object('triggerOid', t.oid, 'functionOid', p.oid,
                    'owner', p.proowner, 'privileges', p.proacl, 'securityDefiner', p.prosecdef,
                    'body', p.prosrc, 'definition', pg_get_triggerdef(t.oid), 'enabled', t.tgenabled) ORDER BY t.tgname)
                    FROM pg_trigger t JOIN pg_proc p ON p.oid = t.tgfoid
                    WHERE t.tgrelid IN ('"{_schema}".ai_execution_policies'::regclass, '"{_schema}".ai_execution_attempts'::regclass)
                        AND t.tgname IN ('guard_ai_financial_policy', 'guard_ai_financial_attempt')))::text
            """, connection);
        return (string)(await query.ExecuteScalarAsync())!;
    }

    private async Task<long> FinancialCountAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var query = new NpgsqlCommand($"SELECT COUNT(*) FROM \"{_schema}\".ai_financial_invocations", connection);
        return (long)(await query.ExecuteScalarAsync())!;
    }
    private async Task<string> FinancialJsonAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var query = new NpgsqlCommand($"SELECT COALESCE(jsonb_agg(to_jsonb(f)), '[]'::jsonb)::text FROM \"{_schema}\".ai_financial_invocations f", connection);
        return (string)(await query.ExecuteScalarAsync())!;
    }
    private async Task<(long Tokens, long Money, string State)> FinancialChargeAsync(Guid id)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var query = new NpgsqlCommand($"SELECT charged_tokens,charged_money,state FROM \"{_schema}\".ai_financial_invocations WHERE attempt_id=@id", connection);
        query.Parameters.AddWithValue("id", id); await using var reader = await query.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue(); return (reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2));
    }
}
