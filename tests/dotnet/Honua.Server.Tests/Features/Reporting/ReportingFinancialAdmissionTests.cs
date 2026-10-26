// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Reporting;
using Honua.Core.Features.Reporting.Domain;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Admission;
using Honua.Db.Postgres.Features.Security.Admission;
using Honua.Infrastructure.Security.Admission;
using Honua.Server.Features.Reporting;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Server.Tests.Features.Reporting;

[Collection("Database.CoreEndpoints")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Update)]
public sealed class ReportingFinancialAdmissionTests(DatabaseFixtureAdapter fixture) : IAsyncLifetime
{
    private string _schema = null!;
    private NpgsqlDataSource _source = null!;
    private PostgresAiExecutionAdmissionStore _store = null!;
    private ServiceProvider _services = null!;
    private IAiExecutionAdmissionStore _selectedStore = null!;
    private AiAdmissionPolicy _policy = null!;

    public async Task InitializeAsync()
    {
        _schema = await fixture.CreateIsolatedSchemaAsync(nameof(ReportingFinancialAdmissionTests));
        _source = NpgsqlDataSource.Create(fixture.ConnectionString);
        await using var connection = await _source.OpenConnectionAsync();
        foreach (var name in new[] { "140_CreateAiExecutionAdmission.sql", "141_CreateAiFinancialAccounting.sql" })
        {
            await using var resource = typeof(OpenAiNarrativeProvider).Assembly.GetManifestResourceStream("Honua.Server.Migrations." + name)!;
            using var reader = new StreamReader(resource);
            var sql = (await reader.ReadToEndAsync()).Replace("$HonuaSchema$", $"\"{_schema}\"", StringComparison.Ordinal);
            await using var command = new NpgsqlCommand(sql, connection); await command.ExecuteNonQueryAsync();
        }
        _store = new(new Honua.TestKit.Infrastructure.TestDatabaseConnectionProvider(_source), _schema);
        _selectedStore = _store;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["AiExecutionAdmission:DeploymentId"] = "deployment", ["AiExecutionAdmission:Backend"] = "postgres" }).Build();
        var services = new ServiceCollection(); services.AddLogging();
        services.AddKeyedScoped<IAiExecutionAdmissionStore>("postgres", (_, _) => _selectedStore); services.AddAiExecutionAdmission(configuration);
        _services = services.BuildServiceProvider();
        _policy = AiFinancialTestPolicy.Create("https://api.openai.com/v1/chat/completions", "gpt-4o-mini", "openai");
        var initialized = await _store.ApplyControlAsync(new("deployment", AiAdmissionControlAction.Initialize, Guid.Empty, 0, 0,
            "test:operator", Guid.NewGuid().ToString("N"), "test", _policy), default);
        initialized.Applied.Should().BeTrue();
        var state = initialized.State!;
        (await _store.ApplyControlAsync(new("deployment", AiAdmissionControlAction.Resume, state.Generation, state.Revision, state.Epoch,
            "test:operator", Guid.NewGuid().ToString("N"), "test"), default)).Applied.Should().BeTrue();
    }
    public async Task DisposeAsync()
    {
        await _services.DisposeAsync(); await _source.DisposeAsync(); await fixture.DropSchemaAsync(_schema);
    }

    [IntegrationTest]
    public async Task Generate_StandardModernUsage_ChargesActualAndCacheMakesNoSecondSend()
    {
        using var handler = new ProviderHandler(Body("response-normal", "default", "gpt-4o-mini", """
            {"prompt_tokens":2,"completion_tokens":1,"total_tokens":3,
             "prompt_tokens_details":{"text_tokens":2,"audio_tokens":0,"image_tokens":0,"cached_tokens":0,"cache_write_tokens":0},
             "completion_tokens_details":{"text_tokens":1,"audio_tokens":0,"reasoning_tokens":0,"accepted_prediction_tokens":0,"rejected_prediction_tokens":0}}
            """));
        using var client = new HttpClient(handler); var factory = new Factory(client);
        using var cache = new MemoryCache(new MemoryCacheOptions()); var provider = Provider(factory, cache);
        (await provider.GenerateAsync(Draft(), default)).SlotText["summary"].Should().Be("Synthetic narrative");
        (await Provider(factory, cache).GenerateAsync(Draft(), default)).SlotText.Should().NotBeEmpty();
        handler.Sends.Should().Be(1); factory.Clients.Should().Be(1);
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("service_tier").GetString().Should().Be("default");
        body.RootElement.GetProperty("max_tokens").GetInt32().Should().Be(5, "the financial cap is stricter than Reporting's configured 1024");
        var row = await Row(); row.State.Should().Be("settled"); row.Tokens.Should().Be(3); row.Money.Should().Be(3);
        row.ResponseId.Should().Be("response-normal"); row.Raw.Should().NotContain("fake-api-key").And.NotContain("Deterministic summary");
    }

    [IntegrationTest]
    public async Task Generate_StricterReportingOutputLimit_IsPreservedInActualWireAndReservation()
    {
        using var handler = new ProviderHandler(Body("response-strict", "default", "gpt-4o-mini", """{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}"""));
        using var client = new HttpClient(handler); using var cache = new MemoryCache(new MemoryCacheOptions());
        (await Provider(new Factory(client), cache, maxTokens: 2).GenerateAsync(Draft(), default)).SlotText.Should().NotBeEmpty();
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("max_tokens").GetInt32().Should().Be(2);
        await using var connection = await _source.OpenConnectionAsync();
        await using var query = new NpgsqlCommand($"SELECT reserved_output FROM \"{_schema}\".ai_financial_invocations", connection);
        ((long)(await query.ExecuteScalarAsync())!).Should().Be(2);
    }

    [IntegrationTheory]
    [InlineData(null, "default", "gpt-4o-mini", "send-committed", false)]
    [InlineData("{\"prompt_tokens\":2,\"completion_tokens\":1,\"audio_fee\":1}", "default", "gpt-4o-mini", "send-committed", false)]
    [InlineData("{\"prompt_tokens\":2,\"completion_tokens\":1,\"prompt_tokens_details\":{\"cache_write_tokens\":1}}", "default", "gpt-4o-mini", "send-committed", false)]
    [InlineData("{\"prompt_tokens\":2,\"completion_tokens\":1}", "priority", "gpt-4o-mini", "overrun", true)]
    [InlineData("{\"prompt_tokens\":2,\"completion_tokens\":1}", "default", "different-model", "overrun", true)]
    [InlineData("{\"prompt_tokens\":1000000000000000000000000,\"completion_tokens\":1}", "default", "gpt-4o-mini", "overrun", true)]
    public async Task Generate_MissingUnsupportedOrBreachedReceipt_RetainsFullLiability(string? usage, string tier, string model, string expectedState, bool stopped)
    {
        using var handler = new ProviderHandler(Body("response-unsupported", tier, model, usage)); using var client = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions()); var provider = Provider(new Factory(client), cache);
        (await provider.GenerateAsync(Draft(), default)).SlotText.Should().NotBeEmpty();
        var row = await Row(); row.State.Should().Be(expectedState); row.Tokens.Should().Be(15); row.Money.Should().Be(15);
        (await _store.GetStateAsync("deployment", default))!.Stopped.Should().Be(stopped);
    }

    [IntegrationTest]
    public async Task Generate_ProviderFailure_RetainsLiabilityForDeterministicFallback()
    {
        using var handler = new ProviderHandler("FAKE_PRIVATE_ERROR_BODY", HttpStatusCode.ServiceUnavailable); using var client = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions()); var provider = Provider(new Factory(client), cache);
        var call = () => provider.GenerateAsync(Draft(), default);
        (await call.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().NotContain("FAKE_PRIVATE_ERROR_BODY");
        var row = await Row(); row.State.Should().Be("send-committed"); row.Tokens.Should().Be(15); row.Money.Should().Be(15);
    }

    [IntegrationTest]
    public async Task Generate_ExpiredBasis_DeniesBeforeClientOrCredentialHeaderAndSend()
    {
        var state = (await _store.GetStateAsync("deployment", default))!;
        var stop = await _store.ApplyControlAsync(new("deployment", AiAdmissionControlAction.Stop, state.Generation, state.Revision, state.Epoch,
            "test:operator", Guid.NewGuid().ToString("N"), "test"), default);
        var expired = _policy with { Financial = _policy.Financial! with { Basis = _policy.Financial!.Basis with { ExpiresUtc = DateTimeOffset.UnixEpoch.AddSeconds(1) } } };
        state = stop.State!;
        var update = await _store.ApplyControlAsync(new("deployment", AiAdmissionControlAction.UpdatePolicy, state.Generation, state.Revision, state.Epoch,
            "test:operator", Guid.NewGuid().ToString("N"), "test", expired), default); state = update.State!;
        (await _store.ApplyControlAsync(new("deployment", AiAdmissionControlAction.Resume, state.Generation, state.Revision, state.Epoch,
            "test:operator", Guid.NewGuid().ToString("N"), "test"), default)).Applied.Should().BeTrue();
        using var handler = new ProviderHandler("must not send"); using var client = new HttpClient(handler); var factory = new Factory(client);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        (await Provider(factory, cache).GenerateAsync(Draft(), default)).SlotText.Should().BeEmpty();
        factory.Clients.Should().Be(0); handler.Sends.Should().Be(0);
        await using var connection = await _source.OpenConnectionAsync(); await using var count = new NpgsqlCommand($"SELECT count(*) FROM \"{_schema}\".ai_financial_invocations", connection);
        ((long)(await count.ExecuteScalarAsync())!).Should().Be(0);
    }

    [IntegrationTheory]
    [InlineData("{\"prompt_tokens\":20}", 20L, null)]
    [InlineData("{\"prompt_tokens\":\"invalid\",\"completion_tokens\":20}", null, 20L)]
    [InlineData("{\"prompt_tokens\":20,\"completion_tokens\":-1}", 20L, null)]
    [InlineData("{\"prompt_tokens\":1,\"prompt_tokens\":2,\"completion_tokens\":20}", null, 20L)]
    [InlineData("{\"prompt_tokens\":1000000000000000000000000,\"completion_tokens\":20}", null, 20L)]
    [InlineData("{\"prompt_tokens\":20,\"completion_tokens\":1000000000000000000000000}", 20L, null)]
    public async Task Generate_KnownCounterOverrunWithIncompleteCounter_RetainsKnownLowerBoundAndStops(string usage, long? knownInput, long? knownOutput)
    {
        using var handler = new ProviderHandler(Body("response-partial-overrun", "default", "gpt-4o-mini", usage));
        using var client = new HttpClient(handler); using var cache = new MemoryCache(new MemoryCacheOptions());
        (await Provider(new Factory(client), cache).GenerateAsync(Draft(), default)).SlotText.Should().NotBeEmpty();
        handler.Sends.Should().Be(1);
        var row = await Row(); row.State.Should().Be("overrun"); var retained = Math.Max(10, knownInput ?? 0) + Math.Max(5, knownOutput ?? 0);
        row.Tokens.Should().Be(retained); row.Money.Should().Be(retained);
        using var evidence = JsonDocument.Parse(row.Raw);
        if (knownInput is { } input) evidence.RootElement.GetProperty("actual_input").GetInt64().Should().Be(input);
        else evidence.RootElement.GetProperty("actual_input").ValueKind.Should().Be(JsonValueKind.Null, "missing or invalid input is not an observed zero");
        if (knownOutput is { } output) evidence.RootElement.GetProperty("actual_output").GetInt64().Should().Be(output);
        else evidence.RootElement.GetProperty("actual_output").ValueKind.Should().Be(JsonValueKind.Null, "missing or invalid output is not an observed zero");
        (await _store.GetStateAsync("deployment", default))!.Stopped.Should().BeTrue();
    }

    [IntegrationTest]
    public async Task Generate_KnownOverrunWithUnknownDimension_RetainsObservedDebtAndStopsSpending()
    {
        using var handler = new ProviderHandler(Body("response-masked-overrun", "default", "gpt-4o-mini", """{"prompt_tokens":20,"completion_tokens":1,"audio_fee":1}"""));
        using var client = new HttpClient(handler); using var cache = new MemoryCache(new MemoryCacheOptions());
        (await Provider(new Factory(client), cache).GenerateAsync(Draft(), default)).SlotText.Should().NotBeEmpty();
        handler.Sends.Should().Be(1);
        var row = await Row(); row.State.Should().Be("overrun"); row.Tokens.Should().BeGreaterThanOrEqualTo(21); row.Money.Should().BeGreaterThanOrEqualTo(21);
        (await _store.GetStateAsync("deployment", default))!.Stopped.Should().BeTrue();
    }

    [IntegrationTheory]
    [InlineData("priority", "gpt-4o-mini")]
    [InlineData("default", "different-model")]
    public async Task Generate_RootContractBreach_RetainsKnownCounterLiability(string tier, string model)
    {
        using var handler = new ProviderHandler(Body("root-breach", tier, model, "{\"prompt_tokens\":100,\"completion_tokens\":1}"));
        using var client = new HttpClient(handler); using var cache = new MemoryCache(new MemoryCacheOptions());
        (await Provider(new Factory(client), cache).GenerateAsync(Draft(), default)).SlotText.Should().NotBeEmpty();
        var row = await Row(); row.State.Should().Be("overrun"); row.Tokens.Should().Be(105); row.Money.Should().Be(105);
        using var evidence = JsonDocument.Parse(row.Raw);
        evidence.RootElement.GetProperty("actual_input").GetInt64().Should().Be(100);
        evidence.RootElement.GetProperty("actual_output").GetInt64().Should().Be(1);
        (await _store.GetStateAsync("deployment", default))!.Stopped.Should().BeTrue();
    }

    [IntegrationTest]
    public async Task Generate_ReportingTimeoutAfterImmediateHeaders_CancelsBodyBeforeAdmissionDeadline()
    {
        using var cleanup = new CancellationTokenSource();
        using var blocked = new BlockedResponseStream(); using var handler = new BlockingHandler(blocked); using var client = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var call = Provider(new Factory(client), cache, timeoutSeconds: 5).GenerateAsync(Draft(), cleanup.Token);
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        handler.Sends.Should().Be(1, "headers and a real financial send commitment must precede this timeout probe");
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        Exception? observed = null;
        bool cancelledBeforeCleanup;
        try { await call.WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (Exception error) { observed = error; }
        finally
        {
            elapsed.Stop();
            cancelledBeforeCleanup = blocked.Cancelled && !cleanup.IsCancellationRequested;
            // Always drain the deliberate blocking fixture, including when the old
            // implementation misses its timeout and the bounded observation expires.
            await cleanup.CancelAsync();
            try { await call.WaitAsync(TimeSpan.FromSeconds(6)); }
            catch (OperationCanceledException) { }
        }
        cancelledBeforeCleanup.Should().BeTrue("the configured Reporting timeout must cancel body transport without caller abort or stop");
        observed.Should().BeAssignableTo<OperationCanceledException>();
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8));
        var row = await Row(); row.State.Should().Be("send-committed"); row.Tokens.Should().Be(15); row.Money.Should().Be(15);
        await AssertConcurrencyReleased();
    }

    [IntegrationTest]
    public async Task Generate_CommittedFinancialClaimLosesAcknowledgement_SendsNothingAndRetainsFullDebt()
    {
        var ambiguous = new AmbiguousCommitStore(_store); _selectedStore = ambiguous;
        using var handler = new ProviderHandler("must not send"); using var client = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        (await Provider(new Factory(client), cache).GenerateAsync(Draft(), default)).SlotText.Should().BeEmpty();
        ambiguous.Claims.Should().Be(1, "the committed-but-uncertain authorization must never be retried");
        handler.Sends.Should().Be(0);
        var row = await Row(); row.State.Should().Be("send-committed"); row.Tokens.Should().Be(15); row.Money.Should().Be(15);
        await AssertConcurrencyReleased();
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generate_CancelOrStopDuringResponseBody_CancelsTransportAndRetainsFullDebt(bool stop)
    {
        using var cancellation = new CancellationTokenSource();
        using var blocked = new BlockedResponseStream(); using var handler = new BlockingHandler(blocked); using var client = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var call = Provider(new Factory(client), cache).GenerateAsync(Draft(), cancellation.Token);
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (stop)
        {
            var state = (await _store.GetStateAsync("deployment", default))!;
            (await _store.ApplyControlAsync(new("deployment", AiAdmissionControlAction.Stop, state.Generation, state.Revision, state.Epoch,
                "test:operator", Guid.NewGuid().ToString("N"), "test"), default)).Applied.Should().BeTrue();
        }
        else await cancellation.CancelAsync();
        var awaitCall = async () => await call.WaitAsync(TimeSpan.FromSeconds(10));
        await awaitCall.Should().ThrowAsync<OperationCanceledException>();
        blocked.Cancelled.Should().BeTrue(); handler.Sends.Should().Be(1);
        var row = await Row(); row.State.Should().Be("send-committed"); row.Tokens.Should().Be(15); row.Money.Should().Be(15);
        await AssertConcurrencyReleased();
    }

    private async Task AssertConcurrencyReleased()
    {
        await using var connection = await _source.OpenConnectionAsync();
        await using var query = new NpgsqlCommand($"SELECT count(*) FROM \"{_schema}\".ai_execution_attempts WHERE released_at IS NOT NULL", connection);
        ((long)(await query.ExecuteScalarAsync())!).Should().Be(1);
    }

    private OpenAiNarrativeProvider Provider(IHttpClientFactory factory, IMemoryCache cache, int maxTokens = 1024, int timeoutSeconds = 15) => new(factory, cache,
        Options.Create(new ReportingConfiguration
        {
            Narrative = new()
            {
                Enabled = true,
                MaxTokens = maxTokens,
                TimeoutSeconds = timeoutSeconds,
                ApiKey = "fake-api-key",
                Disclosure = new()
                { Approvals = [new() { Provider = "openai", Kind = "openai", Destination = "https://api.openai.com/v1", Model = "gpt-4o-mini", UseCase = "reporting-narrative", DataClassification = "unclassified" }] }
            }
        }),
        NullLogger<OpenAiNarrativeProvider>.Instance, _services.GetRequiredService<IAiExecutionAdmission>());
    private static AnalysisReportDraft Draft() => new()
    {
        ExecutionContext = AiExecutionContext.Create("deployment", "ApiKey:verified-fixture", "reporting-narrative", "job:verified"),
        TemplateId = "financial-test",
        TemplateVersion = "1",
        ProcessId = "analytics.buffer-aggregate",
        ProcessFamily = "analytics",
        Sections = [],
        NarrativeSlots = [new() { SlotId = "summary", DeterministicText = "Deterministic summary" }],
        SourcePackage = ReportingFixtures.BufferAggregatePackage()
    };
    private static string Body(string id, string tier, string model, string? usage) =>
        "{\"id\":\"" + id + "\",\"service_tier\":\"" + tier + "\",\"model\":\"" + model + "\",\"choices\":[{\"message\":{\"content\":\"{\\\"slots\\\":{\\\"summary\\\":\\\"Synthetic narrative\\\"}}\"}}]" + (usage is null ? "" : ",\"usage\":" + usage) + "}";
    private async Task<(string State, long Tokens, long Money, string? ResponseId, string Raw)> Row()
    {
        await using var connection = await _source.OpenConnectionAsync();
        await using var query = new NpgsqlCommand($"SELECT state,charged_tokens,charged_money,provider_response_id,to_jsonb(f)::text FROM \"{_schema}\".ai_financial_invocations f", connection);
        await using var reader = await query.ExecuteReaderAsync(); (await reader.ReadAsync()).Should().BeTrue();
        return (reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4));
    }
    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public int Clients { get; private set; }
        public HttpClient CreateClient(string name) { Clients++; return client; }
    }
    private sealed class ProviderHandler(string response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Sends { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Sends++; Body = await request.Content!.ReadAsStringAsync(cancellationToken); return new(status) { Content = new StringContent(response) }; }
    }
    private sealed class AmbiguousCommitStore(IAiExecutionAdmissionStore inner) : IAiExecutionAdmissionStore
    {
        public int Claims { get; private set; }
        public Task<AiAdmissionDecision> TryAcquireAsync(AiExecutionContext context, Guid id, CancellationToken token) => inner.TryAcquireAsync(context, id, token);
        public Task<AiAdmissionDecision> TryAcquireAsync(AiExecutionContext context, Guid id, AiFinancialRequest request, CancellationToken token) => inner.TryAcquireAsync(context, id, request, token);
        public Task<bool> IsCurrentAsync(AiAdmissionReservation reservation, CancellationToken token) => inner.IsCurrentAsync(reservation, token);
        public Task ReleaseAsync(AiAdmissionReservation reservation, CancellationToken token) => inner.ReleaseAsync(reservation, token);
        public Task<AiAdmissionState?> GetStateAsync(string deployment, CancellationToken token) => inner.GetStateAsync(deployment, token);
        public Task<AiAdmissionControlResult> ApplyControlAsync(AiAdmissionControlCommand command, CancellationToken token) => inner.ApplyControlAsync(command, token);
        public Task<string> SettleFinancialAsync(AiAdmissionReservation reservation, AiFinancialUsage usage, CancellationToken token) => inner.SettleFinancialAsync(reservation, usage, token);
        public async Task<bool> CommitFinancialSendAsync(AiAdmissionReservation reservation, CancellationToken token)
        {
            Claims++;
            (await inner.CommitFinancialSendAsync(reservation, token)).Should().BeTrue("the real database commitment must precede the simulated lost acknowledgement");
            throw new IOException("synthetic lost acknowledgement after durable commit");
        }
    }
    private sealed class BlockingHandler(Stream stream) : HttpMessageHandler
    {
        public int Sends { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Sends++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        }
    }
    private sealed class BlockedResponseStream : Stream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
            catch (OperationCanceledException) { Cancelled = true; throw; }
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

}
