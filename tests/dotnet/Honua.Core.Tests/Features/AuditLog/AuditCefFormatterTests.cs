// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Core.Tests.Features.AuditLog;

/// <summary>
/// Unit tests for the CEF SIEM export formatter (#509).
/// </summary>
public sealed class AuditCefFormatterTests
{
    private static AuditEventRecord SampleRecord(
        string action = "layer.delete",
        AuditOutcome outcome = AuditOutcome.Denied,
        string? details = "{\"status\":403}")
        => new()
        {
            AuditId = 42,
            Timestamp = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero),
            EventType = AuditEventType.Authorization,
            Actor = "user-7",
            ActorType = AuditActorType.UserId,
            ResourceType = "layer",
            ResourceId = "roads",
            Action = action,
            Outcome = outcome,
            CorrelationId = "corr-xyz",
            RemoteIp = "10.0.0.9",
            UserAgent = "honua-test/1.0",
            Details = details ?? string.Empty,
        };

    [Fact]
    public void Format_EmitsCefHeader()
    {
        var line = AuditCefFormatter.Format(SampleRecord());
        line.Should().StartWith("CEF:0|Honua|Honua Server|1|layer.delete|Authorization|7|");
    }

    [Fact]
    public void Format_IncludesKeyExtensionFields()
    {
        var line = AuditCefFormatter.Format(SampleRecord());
        line.Should().Contain("externalId=42");
        line.Should().Contain("suser=user-7");
        line.Should().Contain("act=layer.delete");
        line.Should().Contain("outcome=Denied");
        line.Should().Contain("cs1=layer");
        line.Should().Contain("cs2=roads");
        line.Should().Contain("src=10.0.0.9");
    }

    [Fact]
    public void Format_DeniedOutcome_HasHigherSeverityThanSuccess()
    {
        var denied = AuditCefFormatter.Format(SampleRecord(outcome: AuditOutcome.Denied));
        var success = AuditCefFormatter.Format(SampleRecord(outcome: AuditOutcome.Success));
        denied.Should().Contain("|Authorization|7|");
        success.Should().Contain("|Authorization|3|");
    }

    [Fact]
    public void Format_EscapesPipeInHeaderAndEqualsInExtension()
    {
        var record = SampleRecord(action: "weird|action", details: "key=value");
        var line = AuditCefFormatter.Format(record);
        line.Should().Contain("weird\\|action", "pipes in CEF headers must be escaped");
        line.Should().Contain("msg=key\\=value", "equals in CEF extension values must be escaped");
    }

    [Fact]
    public void Format_OmitsEmptyExtensionFields()
    {
        var record = SampleRecord(details: null);
        var line = AuditCefFormatter.Format(record);
        line.Should().NotContain("msg=");
    }
}
