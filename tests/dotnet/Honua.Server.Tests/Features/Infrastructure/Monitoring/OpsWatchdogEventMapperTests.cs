// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Observability.Domain;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

[Protocol(TestProtocols.TestQuality)]
public sealed class OpsWatchdogEventMapperTests
{
    [UnitTheory]
    [InlineData("needs_person", "Critical", OpsFindingSeverity.Critical)]
    [InlineData("action:unknown", "Warning", OpsFindingSeverity.Warning)]
    [InlineData("recovered", "Info", OpsFindingSeverity.Info)]
    [Operation(Operations.TestInfrastructure)]
    public void Map_Incident_PreservesChronologyIdentityAndMessage(string stage, string severity, OpsFindingSeverity expected)
    {
        using var document = Envelope(stage, severity);

        var finding = OpsWatchdogEventMapper.Map(document.RootElement);

        finding.Should().NotBeNull();
        finding!.Rule.Should().Be("watchdog-incident");
        finding.Severity.Should().Be(expected);
        finding.Subject.TargetId.Should().Be("arn:aws:ecs:us-east-1:123456789012:service/cluster/server");
        finding.Subject.OperationId.Should().Be("18425bb2-1777-4560-b17f-5e24dcb4d176");
        finding.DetectedAt.Should().Be(DateTimeOffset.Parse("2026-11-14T03:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        finding.Explanation.Should().Contain("Recovery observed after 360 seconds");
        finding.EvidenceRefs.Should().Contain("aws-event:watchdog-event-1");
        finding.RecommendedAction.Should().BeNull();
    }

    [UnitTheory]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":2")]
    [InlineData("Honua watchdog incident", "Unknown event")]
    [InlineData("honua.watchdog", "unknown.source")]
    [InlineData("18425bb2-1777-4560-b17f-5e24dcb4d176", "")]
    [InlineData("\"severity\":\"Info\"", "\"severity\":\"unknown\"")]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":\"1\"")]
    [InlineData("\"stage\":\"recovered\"", "\"stage\":\"unknown\"")]
    [InlineData("\"stage\":\"recovered\"", "\"stage\":\"diagnosed:\"")]
    [InlineData("18425bb2-1777-4560-b17f-5e24dcb4d176", "not-an-incident-id")]
    [InlineData("2026-11-14T03:00:00Z", "invalid-time")]
    [InlineData(":service/cluster/server", ":task/cluster/server")]
    [Operation(Operations.TestInfrastructure)]
    public void Map_UnsupportedOrIncompleteIncident_DoesNotInventFinding(string original, string replacement)
    {
        using var originalDocument = Envelope("recovered", "Info");
        using var document = JsonDocument.Parse(originalDocument.RootElement.GetRawText().Replace(original, replacement, StringComparison.Ordinal));

        OpsWatchdogEventMapper.Map(document.RootElement).Should().BeNull();
    }

    [UnitTheory]
    [InlineData("message")]
    [InlineData("stage")]
    [Operation(Operations.TestInfrastructure)]
    public void Map_OversizedFreeText_DoesNotInventFinding(string field)
    {
        using var originalDocument = Envelope("recovered", "Info");
        var raw = originalDocument.RootElement.GetRawText();
        raw = field == "message"
            ? raw.Replace("Recovery observed after 360 seconds; requested restart preceded recovery.", new string('x', 1025), StringComparison.Ordinal)
            : raw.Replace("\"stage\":\"recovered\"", $"\"stage\":\"diagnosed:{new string('x', 128)}\"", StringComparison.Ordinal);
        using var document = JsonDocument.Parse(raw);

        OpsWatchdogEventMapper.Map(document.RootElement).Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Map_ExternalEventPipeline_ProjectsWatchdogIncident()
    {
        using var document = Envelope("needs_person", "Critical");

        var finding = OpsExternalEventMapper.Map(document.RootElement.GetRawText());

        finding.Should().NotBeNull();
        finding!.Rule.Should().Be("watchdog-incident");
        finding.Severity.Should().Be(OpsFindingSeverity.Critical);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void MapRecent_RecoveredIncident_RetainsOtherIncidentAndDeduplicatesReplay()
    {
        using var document = Envelope("needs_person", "Critical");
        var body = document.RootElement.GetRawText();
        var original = OpsExternalEventMapper.Parse(body);
        var other = OpsExternalEventMapper.Parse(body.Replace("watchdog-event-1", "watchdog-event-2", StringComparison.Ordinal)
            .Replace("18425bb2-1777-4560-b17f-5e24dcb4d176", "28425bb2-1777-4560-b17f-5e24dcb4d176", StringComparison.Ordinal));
        var recovered = OpsExternalEventMapper.Parse(body.Replace("watchdog-event-1", "watchdog-event-3", StringComparison.Ordinal)
            .Replace("needs_person", "recovered", StringComparison.Ordinal).Replace("Critical", "Info", StringComparison.Ordinal)
            .Replace("03:00:00Z", "03:01:00Z", StringComparison.Ordinal));

        var findings = OpsExternalEventMapper.MapRecent([recovered, original, other, recovered], original.OccurredAt.AddMinutes(2));

        findings.Should().HaveCount(2);
        findings.Should().ContainSingle(f => f.Subject.OperationId == "18425bb2-1777-4560-b17f-5e24dcb4d176")
            .Which.Severity.Should().Be(OpsFindingSeverity.Info);
        findings.Should().ContainSingle(f => f.Subject.OperationId == "28425bb2-1777-4560-b17f-5e24dcb4d176")
            .Which.Severity.Should().Be(OpsFindingSeverity.Critical);
    }

    [UnitTheory]
    [InlineData("diagnosed:server_unhealthy", "action:unknown", "Critical")]
    [InlineData("diagnosed:server_unhealthy", "action:rate_limited", "Critical")]
    [InlineData("action:unknown", "recovered", "Info")]
    [InlineData("needs_person", "recovered", "Info")]
    [Operation(Operations.TestInfrastructure)]
    public void MapRecent_EqualTimestampStages_PreservesCausalOrder(
        string earlierStage, string laterStage, string laterSeverity)
    {
        using var earlierDocument = Envelope(earlierStage, "Warning");
        using var laterDocument = Envelope(laterStage, laterSeverity);
        foreach (var suffix in new[] { "a", "b" })
        {
            var earlier = OpsExternalEventMapper.Parse(earlierDocument.RootElement.GetRawText()
                .Replace("watchdog-event-1", $"earlier-{suffix}", StringComparison.Ordinal));
            var later = OpsExternalEventMapper.Parse(laterDocument.RootElement.GetRawText()
                .Replace("watchdog-event-1", $"later-{suffix}", StringComparison.Ordinal));

            AssertLatestStage([earlier, later], laterStage);
            AssertLatestStage([later, earlier], laterStage);
        }
    }

    private static void AssertLatestStage(IReadOnlyList<OpsExternalEvent> events, string stage)
    {
        var findings = OpsExternalEventMapper.MapRecent(events, events[0].OccurredAt.AddMinutes(1));
        findings.Should().ContainSingle().Which.Title.Should().Be($"Watchdog incident: {stage}");
    }

    private static JsonDocument Envelope(string stage, string severity) => JsonDocument.Parse($$$$$"""
        {"version":"0","id":"watchdog-event-1","source":"honua.watchdog","detail-type":"Honua watchdog incident",
         "time":"2026-11-14T03:00:00Z","region":"us-east-1",
         "resources":["arn:aws:ecs:us-east-1:123456789012:service/cluster/server"],
         "detail":{"schemaVersion":1,"stackName":"example","incidentId":"18425bb2-1777-4560-b17f-5e24dcb4d176",
         "stage":"{{{{{stage}}}}}","severity":"{{{{{severity}}}}}","diagnosis":"server_unhealthy","action":"restart",
         "message":"Recovery observed after 360 seconds; requested restart preceded recovery.","availability":"Standard",
         "recovery":{"durationSeconds":360,"lastAction":{"type":"restart","at":1794624840,"outcome":"requested"}}}}
        """);
}
