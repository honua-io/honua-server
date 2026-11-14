// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Observability.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

[Protocol(TestProtocols.TestQuality)]
public sealed class OpsExternalEventTests
{
    [UnitTheory]
    [InlineData("aws.ecs", "ECS Task State Change", "{\"lastStatus\":\"STOPPED\",\"containers\":[{\"exitCode\":137,\"reason\":\"OutOfMemoryError\"}]}", "ecs-task-oom")]
    [InlineData("aws.ecs", "ECS Task State Change", "{\"lastStatus\":\"STOPPED\",\"stoppedReason\":\"CannotPullContainerError: denied\"}", "image-pull-failed")]
    [InlineData("aws.ecs", "ECS Task State Change", "{\"lastStatus\":\"STOPPED\",\"stoppedReason\":\"Task failed to start\",\"containers\":[{\"reason\":\"CannotPullContainerError: denied\"}]}", "image-pull-failed")]
    [InlineData("aws.ecs", "ECS Deployment State Change", "{\"eventName\":\"SERVICE_DEPLOYMENT_FAILED\"}", "ecs-deployment-failed")]
    [InlineData("aws.ecs", "ECS Deployment State Change", "{\"eventName\":\"SERVICE_DEPLOYMENT_COMPLETED\"}", "ecs-deployment-completed")]
    [InlineData("aws.backup", "Backup Job State Change", "{\"state\":\"FAILED\"}", "backup-job-failed")]
    [InlineData("aws.backup", "Copy Job State Change", "{\"state\":\"FAILED\"}", "backup-copy-failed")]
    [InlineData("aws.rds", "RDS DB Instance Event", "{\"EventCategories\":[\"maintenance\"]}", "rds-maintenance")]
    [InlineData("aws.batch", "Batch Job State Change", "{\"status\":\"FAILED\"}", "gp-job-failed")]
    [InlineData("aws.acm", "ACM Certificate Approaching Expiration", "{}", "certificate-expiring")]
    [InlineData("aws.health", "AWS Health Event", "{}", "aws-health-event")]
    [InlineData("aws.ce", "Anomaly Detected", "{}", "cost-anomaly")]
    [InlineData("aws.cloudwatch", "CloudWatch Alarm State Change", "{\"state\":{\"value\":\"ALARM\"}}", "cloudwatch-alarm")]
    [Operation(Operations.TestInfrastructure)]
    public void Mapping_HasStableIdentityAndPlainLanguageTemplate(string source, string type, string detail, string rule)
    {
        var body = Envelope(source, type, detail);
        var finding = OpsExternalEventMapper.Map(body);
        finding.Should().NotBeNull();
        finding!.Rule.Should().Be(rule);
        finding.Title.Should().NotBeNullOrWhiteSpace();
        finding.Explanation.Should().NotBeNullOrWhiteSpace();
        finding.Id.Should().Be(OpsExternalEventMapper.Map(body)!.Id);
        finding.RecommendedAction.Should().BeNull();
    }

    [UnitTheory]
    [InlineData("UserInitiated")]
    [InlineData("ServiceSchedulerInitiated")]
    [InlineData("SpotInterruption")]
    [InlineData("TerminationNotice")]
    [Operation(Operations.TestInfrastructure)]
    public void RequestedShutdown_DoesNotProduceCrashFindings(string stopCode)
    {
        var detail = $$"""{"lastStatus":"STOPPED","stopCode":"{{stopCode}}","containers":[{"exitCode":137}]}""";
        var body = Envelope("aws.ecs", "ECS Task State Change", detail);
        var events = Enumerable.Range(0, 3).Select(i => OpsExternalEventMapper.Parse(
            body.Replace("7a92fa24", $"7a92fa2{i}", StringComparison.Ordinal))).ToArray();
        OpsExternalEventMapper.MapRecent(events, DateTimeOffset.Parse("2026-11-14T00:05:00Z", CultureInfo.InvariantCulture))
            .Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Exit137WithoutMemoryEvidence_IsGenericCrash()
    {
        var body = Envelope("aws.ecs", "ECS Task State Change", """{"lastStatus":"STOPPED","containers":[{"exitCode":137}]}""");
        OpsExternalEventMapper.Map(body)!.Rule.Should().Be("ecs-task-stopped");
    }

    [UnitTheory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not json")]
    [Operation(Operations.TestInfrastructure)]
    public void MalformedEnvelope_IsRejected(string body)
    {
        var parse = () => OpsExternalEventMapper.Parse(body);
        parse.Should().Throw<JsonException>();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task DuplicateDelivery_PersistsOneEventAndAcknowledgesBothReceipts()
    {
        var store = new MemoryStore();
        var queue = new FakeQueue();
        await using var services = new ServiceCollection().AddSingleton<IOpsExternalEventStore>(store).BuildServiceProvider();
        using var service = CreateService(queue, services);
        var body = Envelope("aws.batch", "Batch Job State Change", "{\"status\":\"FAILED\"}");
        await service.ProcessAsync(new OpsEventQueueMessage("first", body), CancellationToken.None);
        await service.ProcessAsync(new OpsEventQueueMessage("second", body), CancellationToken.None);
        store.Events.Should().HaveCount(1);
        queue.Deleted.Should().Equal("first", "second");
        OpsExternalEventMapper.MapRecent(store.Events.Values.ToArray(), DateTimeOffset.UtcNow).Should().ContainSingle();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task StoreFailure_DoesNotAcknowledgeMessage()
    {
        var queue = new FakeQueue();
        await using var services = new ServiceCollection().AddSingleton<IOpsExternalEventStore>(new MemoryStore { Fail = true }).BuildServiceProvider();
        using var service = CreateService(queue, services);
        var process = () => service.ProcessAsync(new OpsEventQueueMessage("receipt", Envelope("aws.health", "AWS Health Event", "{}")), CancellationToken.None);
        await process.Should().ThrowAsync<InvalidOperationException>();
        queue.Deleted.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task PoisonMessage_RemainsForQueueRedrive()
    {
        var queue = new FakeQueue();
        await using var services = new ServiceCollection().AddSingleton<IOpsExternalEventStore>(new MemoryStore()).BuildServiceProvider();
        using var service = CreateService(queue, services);
        var process = () => service.ProcessAsync(new OpsEventQueueMessage("poison", "not json"), CancellationToken.None);
        await process.Should().ThrowAsync<JsonException>();
        queue.Deleted.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ThreeDistinctCrashesInFifteenMinutes_CreateCrashLoopFinding()
    {
        var body = Envelope("aws.ecs", "ECS Task State Change", "{\"lastStatus\":\"STOPPED\",\"clusterArn\":\"cluster\",\"group\":\"service:server\",\"containers\":[{\"exitCode\":1}]}");
        var events = Enumerable.Range(0, 3).Select(i => OpsExternalEventMapper.Parse(body.Replace("7a92fa24", $"7a92fa2{i}", StringComparison.Ordinal))).ToArray();
        var findings = OpsExternalEventMapper.MapRecent(events, DateTimeOffset.Parse("2026-11-14T00:05:00Z", CultureInfo.InvariantCulture));
        findings.Should().ContainSingle(f => f.Rule == "ecs-task-crash-loop" && f.Severity == OpsFindingSeverity.Critical);
        OpsExternalEventMapper.MapRecent(events, DateTimeOffset.Parse("2026-11-14T00:16:00Z", CultureInfo.InvariantCulture))
            .Should().NotContain(f => f.Rule == "ecs-task-crash-loop");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task HandlerFailure_RetriesPersistedEventBeforeAcknowledgment()
    {
        var store = new MemoryStore();
        var handler = new RetryingHandler(store);
        var queue = new FakeQueue();
        await using var services = new ServiceCollection().AddSingleton<IOpsExternalEventStore>(store)
            .AddSingleton<IOpsExternalEventConsumer>(handler).BuildServiceProvider();
        using var service = CreateService(queue, services);
        var body = Envelope("aws.ecs", "ECS Deployment State Change", "{\"eventName\":\"SERVICE_DEPLOYMENT_COMPLETED\"}");
        var process = () => service.ProcessAsync(new OpsEventQueueMessage("first", body), CancellationToken.None);
        await process.Should().ThrowAsync<InvalidOperationException>();
        queue.Deleted.Should().BeEmpty();
        await service.ProcessAsync(new OpsEventQueueMessage("retry", body), CancellationToken.None);
        store.Events.Should().ContainSingle();
        handler.Calls.Should().Be(2);
        queue.Deleted.Should().Equal("retry");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void AlarmRecovery_ReplacesEarlierActiveAlarmRegardlessOfDeliveryOrder()
    {
        var alarm = OpsExternalEventMapper.Parse(Envelope("aws.cloudwatch", "CloudWatch Alarm State Change", "{\"state\":{\"value\":\"ALARM\"}}"));
        var recovered = OpsExternalEventMapper.Parse(alarm.Body.Replace("7a92fa24", "8a92fa24", StringComparison.Ordinal)
            .Replace("00:00:00Z", "00:01:00Z", StringComparison.Ordinal).Replace("ALARM", "OK", StringComparison.Ordinal));
        var findings = OpsExternalEventMapper.MapRecent([recovered, alarm], DateTimeOffset.Parse("2026-11-14T00:05:00Z", CultureInfo.InvariantCulture));
        findings.Should().ContainSingle().Which.Rule.Should().Be("cloudwatch-alarm-recovered");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Map_JsonbSeparatorExpansion_DoesNotRejectAcceptedEvent()
    {
        var detail = "{\"status\":\"FAILED\",\"padding\":[" + string.Join(",", Enumerable.Repeat("0", 100000)) + "]}";
        var wire = Envelope("aws.batch", "Batch Job State Change", detail);
        OpsExternalEventMapper.Parse(wire);
        var persisted = wire.Replace(",", ", ", StringComparison.Ordinal);
        System.Text.Encoding.UTF8.GetByteCount(persisted).Should().BeGreaterThan(262144);
        OpsExternalEventMapper.Map(persisted)!.Rule.Should().Be("gp-job-failed");
        var ingest = () => OpsExternalEventMapper.Parse(persisted);
        ingest.Should().Throw<JsonException>();
    }

    [UnitTheory]
    [InlineData("SERVICE_DEPLOYMENT_COMPLETED", "ecs-deployment-completed")]
    [InlineData("SERVICE_DEPLOYMENT_ROLLBACK_COMPLETED", "ecs-deployment-rollback-completed")]
    [Operation(Operations.TestInfrastructure)]
    public void MapRecent_RecoveredDeployment_RetiresFailureForOnlyThatService(string outcome, string rule)
    {
        var failed = OpsExternalEventMapper.Parse(Envelope("aws.ecs", "ECS Deployment State Change", "{\"eventName\":\"SERVICE_DEPLOYMENT_FAILED\"}"));
        var recovered = OpsExternalEventMapper.Parse(failed.Body.Replace("7a92fa24", "8a92fa24", StringComparison.Ordinal)
            .Replace("00:00:00Z", "00:01:00Z", StringComparison.Ordinal).Replace("SERVICE_DEPLOYMENT_FAILED", outcome, StringComparison.Ordinal));
        var other = OpsExternalEventMapper.Parse(failed.Body.Replace("7a92fa24", "9a92fa24", StringComparison.Ordinal)
            .Replace("service/stack/server", "service/stack/other", StringComparison.Ordinal));
        var findings = OpsExternalEventMapper.MapRecent([recovered, other, failed], DateTimeOffset.Parse("2026-11-14T00:05:00Z", CultureInfo.InvariantCulture));
        findings.Should().HaveCount(2).And.ContainSingle(f => f.Rule == rule);
        findings.Should().ContainSingle(f => f.Rule == "ecs-deployment-failed" && f.Subject.TargetId!.EndsWith("/other", StringComparison.Ordinal));
    }

    private sealed class RetryingHandler(MemoryStore store) : IOpsExternalEventConsumer
    {
        internal int Calls { get; private set; }
        public Task HandleAsync(OpsExternalEvent externalEvent, CancellationToken cancellationToken)
        {
            store.Events.Should().ContainKey(externalEvent.Id);
            if (++Calls == 1)
            {
                throw new InvalidOperationException("Handler temporarily unavailable");
            }

            return Task.CompletedTask;
        }
    }

    private static OpsEventIngestionService CreateService(FakeQueue queue, ServiceProvider services) =>
        new(queue, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<OpsEventIngestionService>.Instance);

    private sealed class MemoryStore : IOpsExternalEventStore
    {
        internal Dictionary<string, OpsExternalEvent> Events { get; } = new(StringComparer.Ordinal);
        internal bool Fail { get; init; }

        public Task AppendAsync(OpsExternalEvent externalEvent, CancellationToken cancellationToken = default)
        {
            if (Fail)
            {
                throw new InvalidOperationException("Database unavailable");
            }

            Events.TryAdd(externalEvent.Id, externalEvent);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OpsExternalEvent>> ReadRecentAsync(DateTimeOffset since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OpsExternalEvent>>(Events.Values.Where(e => e.OccurredAt >= since).ToArray());
    }

    private sealed class FakeQueue : IOpsEventQueueClient
    {
        internal List<string> Deleted { get; } = [];
        public Task<IReadOnlyList<OpsEventQueueMessage>> ReceiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OpsEventQueueMessage>>([]);
        public Task DeleteAsync(string receiptHandle, CancellationToken cancellationToken)
        {
            Deleted.Add(receiptHandle);
            return Task.CompletedTask;
        }
    }

    internal static string Envelope(string source, string type, string detail) =>
        $$"""{"id":"7a92fa24-ff98-4aaa-a495-952e7802d201","source":"{{source}}","detail-type":"{{type}}","time":"2026-11-14T00:00:00Z","resources":["arn:aws:ecs:eu-north-1:123456789012:service/stack/server"],"detail":{{detail}}} """;
}
