// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FluentAssertions;
using Honua.Alerts;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Alerts;
using Honua.Server.Tests.Infrastructure;
using Honua.Server.Tests.Infrastructure.Telemetry;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using NSubstitute;

namespace Honua.Server.Tests.Features.Alerts;

/// <summary>
/// Receiver-side alert delivery evidence. Unlike sink unit tests that replace the HTTP handler,
/// these tests run the production evaluator, outbox writer, dispatcher, retry policy, and webhook
/// sink against a real HTTPS Kestrel socket.
/// </summary>
[Protocol(ProtocolNames.Infrastructure)]
[Operation(Operations.ContractTesting)]
[Collection("Database.Alerts")]
public sealed class AlertWebhookDeliveryE2eTests(DatabaseFixtureAdapter database)
{
    private const long RuleId = 3665;
    private const int LayerId = 7;
    private const long ObjectId = 42;
    private const string ServiceId = "alert-e2e-service";
    private const string SigningSecret = "alert-e2e-signing-secret";

    [IntegrationTest]
    public async Task TriggeredRule_DeliversCanonicalPayloadObservedByExternalWebhookReceiver()
    {
        await using var receiver = await WebhookReceiver.StartAsync();
        await using var harness = await CreateHarnessAsync(receiver.Url, maxAttempts: 3, receiver.Certificate);

        await harness.FireRuleAsync();
        var received = await receiver.WaitForRequestAsync(TimeSpan.FromSeconds(10));
        await harness.WaitForStatusAsync(AlertDispatchStatus.Delivered, TimeSpan.FromSeconds(10));

        using var payload = JsonDocument.Parse(received.Body);
        var root = payload.RootElement;
        root.GetProperty("serviceId").GetString().Should().Be(ServiceId);
        root.GetProperty("layerId").GetInt32().Should().Be(LayerId);
        root.GetProperty("objectId").GetInt64().Should().Be(ObjectId);
        root.GetProperty("ruleId").GetInt64().Should().Be(RuleId);
        root.GetProperty("ruleName").GetString().Should().Be("speed threshold");
        root.GetProperty("trigger").GetString().Should().Be("Threshold");
        root.GetProperty("transition").GetString().Should().Be("threshold");
        root.GetProperty("incidentStatus").GetString().Should().Be("Started");
        root.GetProperty("generation").GetInt64().Should().Be(1);
        root.GetProperty("occurredAt").ValueKind.Should().Be(JsonValueKind.String);

        received.ContentType.Should().StartWith("application/json");
        received.Headers["X-Honua-Alert-Rule"].Should().Be(RuleId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        received.Headers["X-Honua-Alert-Event"].Should().NotBeNullOrWhiteSpace();
        received.Headers["Idempotency-Key"].Should().Be(received.Headers["X-Honua-Alert-Event"]);
        var timestamp = received.Headers["X-Honua-Event-Timestamp"];
        received.Headers["X-Honua-Signature"].Should().Be(
            $"sha256={Honua.Infrastructure.Events.WebhookDeliveryHelper.ComputeSignature(SigningSecret, timestamp, received.Body)}");

        (await harness.PersistDuplicateAsync()).Should().BeNull("the production unique dedupe key suppresses the duplicate event");
        (await harness.CountEventsAsync()).Should().Be(1, "the production unique dedupe key suppresses the duplicate evaluation");
        (await harness.CountDispatchesAsync()).Should().Be(1, "a deduplicated event must not enqueue another delivery");

        var acknowledged = await harness.Lifecycle.AcknowledgeAsync(
            harness.EventId, "candidate-operator", "receipt acknowledged", DateTimeOffset.UtcNow);
        acknowledged!.Status.Should().Be(AlertLifecycleStatus.Acknowledged);
        var resolved = await harness.Lifecycle.ResolveAsync(
            harness.EventId, "candidate-operator", "synthetic condition cleared", DateTimeOffset.UtcNow);
        resolved!.Status.Should().Be(AlertLifecycleStatus.Resolved);
        (await harness.Lifecycle.GetAsync(harness.EventId))!.ResolvedBy.Should().Be("candidate-operator");
    }

    [IntegrationTest]
    public async Task TriggeredRule_WhenWebhookIsDown_RetriesThenDeadLetters()
    {
        var unavailableUrl = ReserveUnavailableHttpsUrl();
        await using var harness = await CreateHarnessAsync(unavailableUrl, maxAttempts: 3);

        await harness.FireRuleAsync();
        await harness.WaitForStatusAsync(AlertDispatchStatus.DeadLetter, TimeSpan.FromSeconds(10));

        (await harness.GetAttemptsAsync()).Should().Be(3, "the retry budget must be exhausted before dead-lettering");
        (await harness.GetLastErrorAsync()).Should().Be("Webhook delivery failed.");
        var backlog = await harness.DispatchStore.GetBacklogAsync();
        backlog.PendingCount.Should().Be(0);
        backlog.DeadLetteredCount.Should().Be(1);
    }

    [IntegrationTest]
    public async Task ClaimedDispatch_WhenWorkerCrashes_IsReclaimedFromProductionOutbox()
    {
        await using var harness = await CreateHarnessAsync(ReserveUnavailableHttpsUrl(), maxAttempts: 3);
        await harness.EvaluateOnlyAsync();

        var firstClaim = await harness.DispatchStore.ClaimPendingAsync(1, DateTimeOffset.UtcNow);
        firstClaim.Should().ContainSingle();
        await harness.ExpireClaimAsync(firstClaim[0].DispatchId);

        var recovered = await harness.DispatchStore.ClaimPendingAsync(1, DateTimeOffset.UtcNow);
        recovered.Should().ContainSingle();
        recovered[0].DispatchId.Should().Be(firstClaim[0].DispatchId);
        recovered[0].Attempts.Should().Be(0, "a worker crash does not consume the provider retry budget");
    }

    [IntegrationTest]
    public async Task OverlappingClaims_StaleWorkerCannotOverwriteWinningTerminalState_InAnyCompletionOrder()
    {
        await using var harness = await CreateHarnessAsync(ReserveUnavailableHttpsUrl(), maxAttempts: 3);
        await harness.EvaluateOnlyAsync();

        foreach (var winnerDelivered in new[] { true, false })
        {
            foreach (var staleCompletesFirst in new[] { true, false })
            {
                await harness.ResetDispatchAsync();
                var workerA = (await harness.DispatchStore.ClaimPendingAsync(1, DateTimeOffset.UtcNow)).Should().ContainSingle().Subject;
                await harness.ExpireClaimAsync(workerA.DispatchId);
                var workerB = (await harness.SecondDispatchStore.ClaimPendingAsync(1, DateTimeOffset.UtcNow)).Should().ContainSingle().Subject;

                workerB.DispatchId.Should().Be(workerA.DispatchId);
                workerB.ClaimToken.Should().NotBe(workerA.ClaimToken);

                async Task<bool> CompleteStaleAsync() => winnerDelivered
                    ? await harness.DispatchStore.MarkFailedAsync(
                        workerA.DispatchId, workerA.ClaimToken, DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow, deadLetter: true, "stale failure")
                    : await harness.DispatchStore.MarkDeliveredAsync(
                        workerA.DispatchId, workerA.ClaimToken, DateTimeOffset.UtcNow);

                async Task<bool> CompleteWinnerAsync() => winnerDelivered
                    ? await harness.SecondDispatchStore.MarkDeliveredAsync(
                        workerB.DispatchId, workerB.ClaimToken, DateTimeOffset.UtcNow)
                    : await harness.SecondDispatchStore.MarkFailedAsync(
                        workerB.DispatchId, workerB.ClaimToken, DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow, deadLetter: true, "winning failure");

                if (staleCompletesFirst)
                {
                    (await CompleteStaleAsync()).Should().BeFalse();
                    (await CompleteWinnerAsync()).Should().BeTrue();
                }
                else
                {
                    (await CompleteWinnerAsync()).Should().BeTrue();
                    (await CompleteStaleAsync()).Should().BeFalse();
                }

                var row = await harness.GetDispatchRowAsync();
                row.Status.Should().Be(winnerDelivered ? AlertDispatchStatus.Delivered : AlertDispatchStatus.DeadLetter);
                row.Attempts.Should().Be(winnerDelivered ? 0 : 1, "only the winning failure may consume an attempt");
                row.LastError.Should().Be(winnerDelivered ? null : "winning failure");
                row.ClaimToken.Should().BeNull();
                if (winnerDelivered)
                {
                    row.DeliveredAt.Should().NotBeNull();
                }
            }
        }
    }

    [IntegrationTest]
    public async Task AcceptedWebhook_WhenAcknowledgementIsNotPersisted_ReplaysWithStableIdempotencyAndConverges()
    {
        await using var receiver = await WebhookReceiver.StartAsync();
        await using var harness = await CreateHarnessAsync(receiver.Url, maxAttempts: 3, receiver.Certificate);
        await harness.EvaluateOnlyAsync();

        var workerA = (await harness.DispatchStore.ClaimPendingAsync(1, DateTimeOffset.UtcNow)).Should().ContainSingle().Subject;
        var alertEvent = await harness.EventStore.GetAsync(workerA.EventId);
        alertEvent.Should().NotBeNull();

        (await harness.Sink.DeliverAsync(workerA, alertEvent!)).Succeeded.Should().BeTrue();
        await harness.ExpireClaimAsync(workerA.DispatchId);

        var workerB = (await harness.SecondDispatchStore.ClaimPendingAsync(1, DateTimeOffset.UtcNow)).Should().ContainSingle().Subject;
        (await harness.Sink.DeliverAsync(workerB, alertEvent!)).Succeeded.Should().BeTrue();
        (await harness.SecondDispatchStore.MarkDeliveredAsync(
            workerB.DispatchId, workerB.ClaimToken, DateTimeOffset.UtcNow)).Should().BeTrue();

        var attempts = await receiver.WaitForRequestsAsync(2, TimeSpan.FromSeconds(10));
        attempts.Select(static request => request.Headers["Idempotency-Key"])
            .Should().OnlyContain(key => key == alertEvent!.DedupeKey);
        receiver.LogicalNotificationCount.Should().Be(1, "the receiver deduplicates the ambiguous replay by its stable key");

        var row = await harness.GetDispatchRowAsync();
        row.Status.Should().Be(AlertDispatchStatus.Delivered);
        row.Attempts.Should().Be(0, "an acknowledged delivery does not consume the failure budget");
        row.LastError.Should().BeNull();
        row.DeliveredAt.Should().NotBeNull();
    }

    private async Task<AlertE2eHarness> CreateHarnessAsync(
        string destination,
        int maxAttempts,
        X509Certificate2? trustedCertificate = null)
    {
        var migrationSchema = await database.CreateIsolatedSchemaAsync(nameof(AlertWebhookDeliveryE2eTests));
        var migration = await database.RunEmbeddedMigrationsUnderLockAsync(
            migrationSchema,
            Assembly.GetAssembly(typeof(Program))!);
        migration.Successful.Should().BeTrue(migration.Error?.ToString());
        await database.DropSchemaAsync(migrationSchema);
        await database.ApplyGlobalSeedSqlAsync("""
            TRUNCATE TABLE honua.alert_event_lifecycle, honua.alert_dispatch, honua.alert_events, honua.alert_rules
            RESTART IDENTITY CASCADE;
            INSERT INTO honua.alert_rules
                (rule_id, service_id, layer_id, rule_name, trigger_type, conditions,
                 severity, edition_required, channels, is_active)
            VALUES
                (3665, 'alert-e2e-service', 7, 'speed threshold', 2,
                 '{"field":"speed","operator":">","value":50}'::jsonb,
                 'warning', 2, ARRAY['webhook'], true);
            """);
        var connectionProvider = new TestConnectionProvider(database.DataSource);
        var outbox = new PostgresAlertOutboxWriter(connectionProvider);
        var dispatchStore = new PostgresAlertDispatchStore(
            connectionProvider,
            NullLogger<PostgresAlertDispatchStore>.Instance);
        var eventStore = new PostgresAlertEventStore(connectionProvider);
        var lifecycleStore = new PostgresAlertLifecycleStore(connectionProvider);
        var alertOptions = new AlertOptions
        {
            Dispatch = new AlertDispatchOptions
            {
                DefaultWebhookUrl = destination,
                DefaultWebhookSecret = SigningSecret,
                IdleDelay = TimeSpan.FromMilliseconds(10),
                InitialBackoff = TimeSpan.Zero,
                MaxBackoff = TimeSpan.Zero,
                CircuitBreakerThreshold = 100,
                MaxNotificationsPerMinutePerChannel = 0,
            },
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Alerts:Enabled"] = "true" })
            .AddEnvironmentVariables()
            .Build();
        configuration.GetSection(AlertOptions.SectionName).Bind(alertOptions);
        var options = Options.Create(alertOptions);

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = static async (context, cancellationToken) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, context.DnsEndPoint.Port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        var trustedCertificateHash = trustedCertificate?.GetCertHashString(HashAlgorithmName.SHA256);
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is not null &&
            trustedCertificateHash is not null &&
            string.Equals(
                certificate.GetCertHashString(HashAlgorithmName.SHA256),
                trustedCertificateHash,
                StringComparison.Ordinal);
        var client = new HttpClient(handler);
        var sink = new WebhookAlertDeliverySink(
            new SingleClientFactory(client),
            options,
            new AlertDestinationGuard(static (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") })));

        var changeReader = Substitute.For<IAlertChangeReader>();
        changeReader.GetChangesAfterAsync(0, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new AlertChange
                {
                    Generation = 1,
                    LayerId = LayerId,
                    ObjectId = ObjectId,
                    Operation = AlertChangeOperation.Update,
                    ChangedAt = DateTimeOffset.UtcNow,
                },
            });

        var rule = new AlertRuleDefinition
        {
            RuleId = RuleId,
            ServiceId = ServiceId,
            LayerId = LayerId,
            RuleName = "speed threshold",
            TriggerType = AlertTriggerType.Threshold,
            ConditionsJson = """{"field":"speed","operator":">","value":50}""",
            Severity = AlertSeverity.Warning,
            EditionRequired = AlertEdition.Pro,
            Channels = ImmutableArray.Create(AlertChannelType.Webhook),
            IsActive = true,
        };
        var rules = Substitute.For<IAlertRuleRepository>();
        rules.GetActiveRulesAsync(Arg.Any<IReadOnlyCollection<AlertRuleLookupKey>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<AlertRuleLookupKey, IReadOnlyList<AlertRuleDefinition>>
            {
                [new AlertRuleLookupKey(ServiceId, LayerId)] = [rule],
            });
        rules.GetZonesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, AlertZoneDefinition>());

        var states = Substitute.For<IAlertStateStore>();
        states.GetManyAsync(Arg.Any<IReadOnlyCollection<AlertStateLookupKey>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<AlertStateLookupKey, AlertStateSnapshot>());

        var featureReader = Substitute.For<IFeatureReader>();
        featureReader.GetAsync(LayerId, ObjectId, Arg.Any<CancellationToken>())
            .Returns(Feature.Create(
                ObjectId,
                geometry: null,
                ImmutableDictionary<string, object?>.Empty.Add("speed", 75d)));

        var editionPolicy = Substitute.For<IAlertEditionPolicy>();
        editionPolicy.IsRuleAllowed(rule).Returns(true);
        editionPolicy.IsChannelAllowed(AlertChannelType.Webhook).Returns(true);
        editionPolicy.IsChannelConfigured(AlertChannelType.Webhook).Returns(true);

        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("resource.alert-e2e", "alerts")
            .AddService("service.alert-e2e", ServiceId)
            .AddPublication("publication.alert-e2e", "service.alert-e2e", "resource.alert-e2e", layerIndex: LayerId)
            .Build();
        var metrics = TestTelemetry.CreateAlertPipelineMetrics();
        var pipeline = new AlertPipeline(
            changeReader,
            rules,
            states,
            featureReader,
            new TestMetadataV2GraphProvider(graph),
            new DefaultAlertEvaluator(),
            editionPolicy,
            new AlertDispatchWriter(outbox, metrics, NullLogger<AlertDispatchWriter>.Instance),
            NullLogger<AlertPipeline>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton<IAlertDispatchStore>(dispatchStore);
        services.AddSingleton<IAlertEventStore>(eventStore);
        services.AddSingleton<IAlertLifecycleStore>(lifecycleStore);
        services.AddSingleton<IAlertEditionPolicy>(editionPolicy);
        var provider = services.BuildServiceProvider();
        var dispatcher = new AlertDispatchBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            [sink],
            new AlertNotificationRateLimiter(),
            new AlertChannelCircuitBreaker(options),
            options,
            metrics,
            NullLogger<AlertDispatchBackgroundService>.Instance);

        return new AlertE2eHarness(
            pipeline, dispatcher, outbox, eventStore, dispatchStore, lifecycleStore,
            database.DataSource, provider, client, sink, maxAttempts);
    }

    private static string ReserveUnavailableHttpsUrl()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"https://alert-e2e.example:{port}/alerts";
    }

    private sealed class AlertE2eHarness(
        AlertPipeline pipeline,
        AlertDispatchBackgroundService dispatcher,
        IAlertOutboxWriter outbox,
        IAlertEventStore eventStore,
        IAlertDispatchStore dispatchStore,
        PostgresAlertLifecycleStore lifecycle,
        NpgsqlDataSource dataSource,
        ServiceProvider provider,
        HttpClient client,
        WebhookAlertDeliverySink sink,
        int maxAttempts) : IAsyncDisposable
    {
        public IAlertDispatchStore DispatchStore { get; } = dispatchStore;
        public IAlertDispatchStore SecondDispatchStore { get; } = new PostgresAlertDispatchStore(
            new TestConnectionProvider(dataSource),
            NullLogger<PostgresAlertDispatchStore>.Instance);
        public IAlertEventStore EventStore { get; } = eventStore;
        public PostgresAlertLifecycleStore Lifecycle { get; } = lifecycle;
        public WebhookAlertDeliverySink Sink { get; } = sink;
        public long EventId { get; private set; }

        public async Task FireRuleAsync()
        {
            await EvaluateOnlyAsync();
            await dispatcher.StartAsync(CancellationToken.None);
        }

        public async Task EvaluateOnlyAsync()
        {
            (await pipeline.ProcessChangesAsync(0, 10, CancellationToken.None)).Should().Be(1);
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("""
                UPDATE honua.alert_dispatch SET max_attempts = @max_attempts;
                SELECT event_id FROM honua.alert_events ORDER BY event_id LIMIT 1;
                """, connection);
            command.Parameters.AddWithValue("max_attempts", NpgsqlDbType.Integer, maxAttempts);
            EventId = (long)(await command.ExecuteScalarAsync())!;
        }

        public async Task WaitForStatusAsync(AlertDispatchStatus status, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (await GetStatusAsync(cts.Token) != status)
            {
                await Task.Delay(10, cts.Token);
            }
        }

        public Task<long> CountEventsAsync() => ScalarAsync<long>("SELECT COUNT(*) FROM honua.alert_events;");
        public Task<long> CountDispatchesAsync() => ScalarAsync<long>("SELECT COUNT(*) FROM honua.alert_dispatch;");
        public async Task<long?> PersistDuplicateAsync()
        {
            var alertEvent = await EventStore.GetAsync(EventId);
            return await outbox.AppendAndEnqueueAsync(alertEvent!, ImmutableArray.Create(AlertChannelType.Webhook));
        }
        public Task<int> GetAttemptsAsync() => ScalarAsync<int>("SELECT attempts FROM honua.alert_dispatch LIMIT 1;");
        public Task<string> GetLastErrorAsync() => ScalarAsync<string>("SELECT last_error FROM honua.alert_dispatch LIMIT 1;");

        public async Task ResetDispatchAsync()
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("""
                UPDATE honua.alert_dispatch
                SET status = 0, attempts = 0, next_attempt_at = now(), last_attempt_at = NULL,
                    delivered_at = NULL, last_error = NULL, claim_token = NULL, updated_at = now();
                """, connection);
            _ = await command.ExecuteNonQueryAsync();
        }

        public async Task<DispatchRow> GetDispatchRowAsync()
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                "SELECT status, attempts, last_error, delivered_at, claim_token FROM honua.alert_dispatch LIMIT 1;",
                connection);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            return new DispatchRow(
                (AlertDispatchStatus)reader.GetInt16(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4));
        }

        public async Task ExpireClaimAsync(long dispatchId)
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                "UPDATE honua.alert_dispatch SET updated_at = now() - INTERVAL '6 minutes' WHERE dispatch_id = @dispatch_id;",
                connection);
            command.Parameters.AddWithValue("dispatch_id", NpgsqlDbType.Bigint, dispatchId);
            _ = await command.ExecuteNonQueryAsync();
        }

        private async Task<AlertDispatchStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            var value = await ScalarAsync<short>("SELECT status FROM honua.alert_dispatch LIMIT 1;", cancellationToken);
            return (AlertDispatchStatus)value;
        }

        private async Task<T> ScalarAsync<T>(string sql, CancellationToken cancellationToken = default)
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            return (T)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        public async ValueTask DisposeAsync()
        {
            await dispatcher.StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
            client.Dispose();
        }
    }

    private sealed record DispatchRow(
        AlertDispatchStatus Status,
        int Attempts,
        string? LastError,
        DateTimeOffset? DeliveredAt,
        Guid? ClaimToken);

    private sealed class TestConnectionProvider(NpgsqlDataSource dataSource) : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;
        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => await dataSource.OpenConnectionAsync(cancellationToken);
        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
            return (connection, transaction);
        }
        public async Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await operation();
        }
        public async Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await operation();
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class WebhookReceiver(WebApplication app, string url, X509Certificate2 certificate) : IAsyncDisposable
    {
        private readonly TaskCompletionSource<ReceivedWebhook> _request = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<ReceivedWebhook> _requests = new();
        private readonly ConcurrentDictionary<string, byte> _logicalNotifications = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _requestSignal = new(0);

        public string Url { get; } = url + "/alerts";
        public X509Certificate2 Certificate { get; } = certificate;
        public int LogicalNotificationCount => _logicalNotifications.Count;

        public static async Task<WebhookReceiver> StartAsync()
        {
            var certificate = CreateCertificate();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(options =>
                options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
            var app = builder.Build();
            WebhookReceiver? receiver = null;
            app.MapPost("/alerts", async context =>
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync(context.RequestAborted);
                var headers = context.Request.Headers.ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value.ToString(),
                    StringComparer.OrdinalIgnoreCase);
                var received = new ReceivedWebhook(
                    body,
                    context.Request.ContentType ?? string.Empty,
                    headers);
                receiver!._requests.Enqueue(received);
                receiver._logicalNotifications.TryAdd(headers["Idempotency-Key"], 0);
                receiver._requestSignal.Release();
                receiver._request.TrySetResult(received);
                context.Response.StatusCode = StatusCodes.Status202Accepted;
            });
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses;
            receiver = new WebhookReceiver(
                app,
                addresses.Single().Replace("127.0.0.1", "alert-e2e.example", StringComparison.Ordinal),
                certificate);
            return receiver;
        }

        public async Task<ReceivedWebhook> WaitForRequestAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            return await _request.Task.WaitAsync(cts.Token);
        }

        public async Task<IReadOnlyList<ReceivedWebhook>> WaitForRequestsAsync(int count, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (_requests.Count < count)
            {
                await _requestSignal.WaitAsync(cts.Token);
            }

            return _requests.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
            _requestSignal.Dispose();
            Certificate.Dispose();
        }

        private static X509Certificate2 CreateCertificate()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("alert-e2e.example");
            request.CertificateExtensions.Add(san.Build());
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        }
    }

    private sealed record ReceivedWebhook(string Body, string ContentType, IReadOnlyDictionary<string, string> Headers);
}
