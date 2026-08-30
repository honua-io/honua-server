// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FluentAssertions;
using Honua.Alerts;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Tests.Infrastructure.Telemetry;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Alerts;

/// <summary>
/// Receiver-side alert delivery evidence. Unlike sink unit tests that replace the HTTP handler,
/// these tests run the production evaluator, outbox writer, dispatcher, retry policy, and webhook
/// sink against a real HTTPS Kestrel socket.
/// </summary>
public sealed class AlertWebhookDeliveryE2eTests
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
        await using var harness = CreateHarness(receiver.Url, maxAttempts: 3, receiver.Certificate);

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
    }

    [IntegrationTest]
    public async Task TriggeredRule_WhenWebhookIsDown_RetriesThenDeadLetters()
    {
        var unavailableUrl = ReserveUnavailableHttpsUrl();
        await using var harness = CreateHarness(unavailableUrl, maxAttempts: 3);

        await harness.FireRuleAsync();
        await harness.WaitForStatusAsync(AlertDispatchStatus.DeadLetter, TimeSpan.FromSeconds(10));

        harness.Store.Attempts.Should().Be(3, "the retry budget must be exhausted before dead-lettering");
        harness.Store.FailureTransitions.Should().Equal(
            AlertDispatchStatus.Failed,
            AlertDispatchStatus.Failed,
            AlertDispatchStatus.DeadLetter);
        harness.Store.LastError.Should().Be("Webhook delivery failed.");
        var backlog = await harness.Store.GetBacklogAsync();
        backlog.PendingCount.Should().Be(0);
        backlog.DeadLetteredCount.Should().Be(1);
    }

    private static AlertE2eHarness CreateHarness(
        string destination,
        int maxAttempts,
        X509Certificate2? trustedCertificate = null)
    {
        var store = new InMemoryAlertOutbox(maxAttempts);
        var options = Options.Create(new AlertOptions
        {
            Enabled = true,
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
        });

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
        var sink = new WebhookAlertDeliverySink(
            new SingleClientFactory(new HttpClient(handler)),
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
            new AlertDispatchWriter(store, metrics, NullLogger<AlertDispatchWriter>.Instance),
            NullLogger<AlertPipeline>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton<IAlertDispatchStore>(store);
        services.AddSingleton<IAlertEventStore>(store);
        services.AddSingleton<IAlertLifecycleStore>(new EmptyLifecycleStore());
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

        return new AlertE2eHarness(pipeline, dispatcher, store, provider, handler);
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
        InMemoryAlertOutbox store,
        ServiceProvider provider,
        SocketsHttpHandler handler) : IAsyncDisposable
    {
        public InMemoryAlertOutbox Store { get; } = store;

        public async Task FireRuleAsync()
        {
            (await pipeline.ProcessChangesAsync(0, 10, CancellationToken.None)).Should().Be(1);
            await dispatcher.StartAsync(CancellationToken.None);
        }

        public async Task WaitForStatusAsync(AlertDispatchStatus status, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (Store.Status != status)
            {
                await Task.Delay(10, cts.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await dispatcher.StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
            handler.Dispose();
        }
    }

    private sealed class InMemoryAlertOutbox(int maxAttempts) : IAlertOutboxWriter, IAlertDispatchStore, IAlertEventStore
    {
        private readonly object _sync = new();
        private AlertEventEnvelope? _event;
        private AlertDispatchItem? _dispatch;
        private long _nextEventId;

        public AlertDispatchStatus? Status { get { lock (_sync) { return _dispatch?.Status; } } }
        public int Attempts { get { lock (_sync) { return _dispatch?.Attempts ?? 0; } } }
        public string? LastError { get; private set; }
        public List<AlertDispatchStatus> FailureTransitions { get; } = [];

        public Task<long?> AppendAndEnqueueAsync(AlertEventEnvelope alertEvent, ImmutableArray<AlertChannelType> channels, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (_event is not null)
                {
                    return Task.FromResult<long?>(null);
                }

                _event = alertEvent;
                _nextEventId = 1;
                _dispatch = new AlertDispatchItem
                {
                    DispatchId = 1,
                    EventId = _nextEventId,
                    ChannelType = channels.Single(),
                    Status = AlertDispatchStatus.Pending,
                    Attempts = 0,
                    MaxAttempts = maxAttempts,
                    NextAttemptAt = DateTimeOffset.UtcNow,
                };
                return Task.FromResult<long?>(_nextEventId);
            }
        }

        public Task<AlertEventEnvelope?> GetAsync(long eventId, CancellationToken cancellationToken = default)
        {
            lock (_sync) { return Task.FromResult(eventId == _nextEventId ? _event : null); }
        }

        public Task<IReadOnlyList<AlertDispatchItem>> ClaimPendingAsync(int maxCount, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (_dispatch is null || _dispatch.NextAttemptAt > now ||
                    _dispatch.Status is not (AlertDispatchStatus.Pending or AlertDispatchStatus.Failed))
                {
                    return Task.FromResult<IReadOnlyList<AlertDispatchItem>>([]);
                }

                var claimed = _dispatch;
                _dispatch = _dispatch with { Status = AlertDispatchStatus.Processing };
                return Task.FromResult<IReadOnlyList<AlertDispatchItem>>([claimed]);
            }
        }

        public Task MarkDeliveredAsync(long dispatchId, DateTimeOffset deliveredAt, CancellationToken cancellationToken = default)
        {
            lock (_sync) { _dispatch = _dispatch! with { Status = AlertDispatchStatus.Delivered }; }
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(long dispatchId, DateTimeOffset attemptedAt, DateTimeOffset nextAttemptAt, bool deadLetter, string? errorMessage, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                var status = deadLetter ? AlertDispatchStatus.DeadLetter : AlertDispatchStatus.Failed;
                _dispatch = _dispatch! with
                {
                    Status = status,
                    Attempts = _dispatch.Attempts + 1,
                    NextAttemptAt = nextAttemptAt,
                };
                LastError = errorMessage;
                FailureTransitions.Add(status);
            }
            return Task.CompletedTask;
        }

        public Task<AlertDispatchBacklog> GetBacklogAsync(CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                return Task.FromResult(new AlertDispatchBacklog
                {
                    PendingCount = _dispatch?.Status is AlertDispatchStatus.Pending or AlertDispatchStatus.Processing or AlertDispatchStatus.Failed ? 1 : 0,
                    RetryingCount = _dispatch?.Status == AlertDispatchStatus.Failed ? 1 : 0,
                    DeadLetteredCount = _dispatch?.Status == AlertDispatchStatus.DeadLetter ? 1 : 0,
                });
            }
        }

        public Task RescheduleAsync(long dispatchId, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken = default)
        {
            lock (_sync) { _dispatch = _dispatch! with { Status = AlertDispatchStatus.Pending, NextAttemptAt = nextAttemptAt }; }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AlertDispatchItem>> ClaimPendingDigestAsync(int maxCount, DateTimeOffset now, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AlertDispatchItem>>([]);
        public Task EnqueueAsync(long eventId, ImmutableArray<AlertChannelType> channels, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long?> TryAppendAsync(AlertEventEnvelope alertEvent, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> PurgeDeliveredAsync(DateTimeOffset deliveredBefore, int batchLimit, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<int> RedriveDeadLettersAsync(DateTimeOffset now, int batchLimit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetChannelPausedAsync(AlertChannelType channel, bool paused, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<AlertChannelType, bool>> GetChannelPauseStatesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<AlertChannelType, bool>>(new Dictionary<AlertChannelType, bool>());
    }

    private sealed class EmptyLifecycleStore : IAlertLifecycleStore
    {
        public Task<AlertEventLifecycle?> GetAsync(long eventId, CancellationToken cancellationToken = default) => Task.FromResult<AlertEventLifecycle?>(null);
        public Task<AlertEventLifecycle?> AcknowledgeAsync(long eventId, string actor, string? note, DateTimeOffset acknowledgedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AlertEventLifecycle?> SuppressAsync(long eventId, string actor, DateTimeOffset suppressUntil, string? note, DateTimeOffset suppressedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AlertEventLifecycle?> ResolveAsync(long eventId, string actor, string? note, DateTimeOffset resolvedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class WebhookReceiver(WebApplication app, string url, X509Certificate2 certificate) : IAsyncDisposable
    {
        private readonly TaskCompletionSource<ReceivedWebhook> _request = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Url { get; } = url + "/alerts";
        public X509Certificate2 Certificate { get; } = certificate;

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
                receiver!._request.TrySetResult(new ReceivedWebhook(
                    body,
                    context.Request.ContentType ?? string.Empty,
                    headers));
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

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
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
