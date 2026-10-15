// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing.CustomCode;
using Honua.Geoprocessing.LocalRunner;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// The server half of honua.gp.events/v1: parsing, bounded forwarding with a disclosed
/// truncation entry, credential redaction on event payloads, measured-vs-inferred
/// timing, and the devkit's local evidence writer.
/// </summary>
public sealed class HarnessEventsTests
{
    private const string Journal = """
        {"v":1,"seq":0,"at":"2026-10-15T04:00:00.000Z","elapsedMs":0,"attempt":"3","kind":"phase","phase":"inputs"}
        {"v":1,"seq":1,"at":"2026-10-15T04:00:00.100Z","elapsedMs":100,"attempt":"3","kind":"progress","percent":50.0,"phase":"transform"}
        {"v":1,"seq":2,"at":"2026-10-15T04:00:00.200Z","elapsedMs":200,"attempt":"3","kind":"log","level":"warning","message":"careful"}
        not a json line
        {"v":1,"seq":3,"at":"2026-10-15T04:00:00.300Z","elapsedMs":300,"attempt":"3","kind":"artifact","artifact":{"name":"out.geojson","sizeBytes":42,"sha256":"ab"}}
        {"v":1,"seq":4,"at":"2026-10-15T04:00:00.400Z","elapsedMs":400,"attempt":"3","kind":"failure","phase":"execute","error":{"type":"ValueError","message":"bad"}}
        {"v":1,"seq":5,"at":"2026-10-15T04:00:00.500Z","elapsedMs":500,"attempt":"3","kind":"terminal","status":"failed","exitCode":1,"phase":"execute"}
        """;

    [Fact]
    public void Parse_KeepsJournalOrder_AndTreatsMalformedLinesAsText()
    {
        var events = HarnessEvents.Parse(Journal);

        events.Select(static e => e.Kind).Should().Equal("phase", "progress", "log", "text", "artifact", "failure", "terminal");
        events[1].Percent.Should().Be(50.0);
        events[1].ElapsedMs.Should().Be(100);
        events[1].Attempt.Should().Be("3");
        events[1].At.Should().Be(new DateTimeOffset(2026, 10, 15, 4, 0, 0, 100, TimeSpan.Zero));
        events[3].Text.Should().Be("not a json line");
        events[4].ArtifactName.Should().Be("out.geojson");
        events[4].ArtifactSizeBytes.Should().Be(42);
        events[5].ErrorType.Should().Be("ValueError");
        events[6].Status.Should().Be("failed");
        events[6].ExitCode.Should().Be(1);
    }

    [Theory]
    [InlineData("{\"v\":2,\"kind\":\"phase\"}")]
    [InlineData("{\"kind\":\"progress\"}")]
    [InlineData("{\"v\":1,\"kind\":\"bogus\"}")]
    [InlineData("[1,2,3]")]
    public void Parse_ForeignRecords_AreText(string line)
    {
        var events = HarnessEvents.Parse(line);

        events.Should().ContainSingle().Which.Kind.Should().Be("text");
        events[0].Text.Should().Be(line);
    }

    [Fact]
    public void ToExecutionLogEntries_ForwardsKindsLevelsAndMeasuredClock()
    {
        var entries = HarnessEvents.ToExecutionLogEntries(HarnessEvents.Parse(Journal), DateTimeOffset.UnixEpoch);

        entries.Select(static e => e.Metadata![HarnessEvents.KindKey]).Should()
            .Equal("phase", "progress", "log", "text", "artifact", "failure", "terminal");
        entries[0].Message.Should().Be("phase inputs");
        entries[1].Message.Should().Be("progress 50.0% — transform");
        entries[1].Metadata![HarnessEvents.PercentKey].Should().Be("50.0");
        entries[1].Metadata![HarnessEvents.ElapsedKey].Should().Be("100");
        entries[1].Metadata![HarnessEvents.AttemptKey].Should().Be("3");
        entries[2].Level.Should().Be(ExecutionLogLevel.Warning);
        entries[3].Timestamp.Should().Be(DateTimeOffset.UnixEpoch, "a text line has no clock");
        entries[5].Level.Should().Be(ExecutionLogLevel.Error);
        entries[5].Message.Should().Be("ValueError: bad");
        entries[6].Level.Should().Be(ExecutionLogLevel.Error);
        entries[6].Metadata!["honua.event.status"].Should().Be("failed");
    }

    [Fact]
    public void ToExecutionLogEntries_CapsAndDisclosesTruncation_KeepingTerminal()
    {
        var lines = Enumerable.Range(0, 20).Select(i =>
            $"{{\"v\":1,\"seq\":{i},\"elapsedMs\":{i},\"attempt\":\"1\",\"kind\":\"log\",\"level\":\"info\",\"message\":\"m{i}\"}}")
            .Append("{\"v\":1,\"seq\":20,\"elapsedMs\":20,\"attempt\":\"1\",\"kind\":\"terminal\",\"status\":\"succeeded\",\"exitCode\":0}");

        var entries = HarnessEvents.ToExecutionLogEntries(HarnessEvents.Parse(lines), DateTimeOffset.UnixEpoch, maxEntries: 5);

        entries.Should().HaveCount(7);
        entries.Take(5).Select(static e => e.Message).Should().Equal("m0", "m1", "m2", "m3", "m4");
        entries[5].Level.Should().Be(ExecutionLogLevel.Warning);
        entries[5].Metadata![HarnessEvents.KindKey].Should().Be("truncated");
        entries[5].Metadata![HarnessEvents.DroppedKey].Should().Be("15");
        entries[5].Message.Should().Contain("15 harness event(s) were not forwarded");
        entries[6].Metadata![HarnessEvents.KindKey].Should().Be("terminal");
    }

    [Fact]
    public void ToExecutionLogEntries_ByteCap_DisclosesTruncation()
    {
        var lines = Enumerable.Range(0, 10).Select(i =>
            $"{{\"v\":1,\"seq\":{i},\"attempt\":\"1\",\"kind\":\"log\",\"level\":\"info\",\"message\":\"{new string('x', 100)}\"}}");

        var entries = HarnessEvents.ToExecutionLogEntries(HarnessEvents.Parse(lines), DateTimeOffset.UnixEpoch, maxBytes: 600);

        entries[^1].Metadata![HarnessEvents.KindKey].Should().Be("truncated");
        (entries.Count - 1).Should().BeInRange(1, 9);
        entries[^1].Metadata![HarnessEvents.DroppedKey].Should().Be((10 - (entries.Count - 1)).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("{\"password\":\"prefix\\\"secret-value\",\"status\":\"running\"}", "secret-value")]
    [InlineData("{\"token\":\"secret-value\",\"status\":\"running\"}", "secret-value")]
    [InlineData("password=\"prefix\\\"secret-suffix", "secret-suffix")]
    [InlineData("token='secret-suffix", "secret-suffix")]
    [InlineData("gdal --config AWS_SECRET_ACCESS_KEY secret-value", "secret-value")]
    public void ToExecutionLogEntries_RedactsCredentials_InLogAndFailurePayloads(string payload, string secret)
    {
        var escaped = payload.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        var lines = new[]
        {
            $"{{\"v\":1,\"seq\":0,\"attempt\":\"1\",\"kind\":\"log\",\"level\":\"info\",\"message\":\"{escaped}\"}}",
            $"{{\"v\":1,\"seq\":1,\"attempt\":\"1\",\"kind\":\"failure\",\"error\":{{\"type\":\"E\",\"message\":\"{escaped}\"}}}}",
        };

        var entries = HarnessEvents.ToExecutionLogEntries(HarnessEvents.Parse(lines), DateTimeOffset.UnixEpoch);

        entries.Should().HaveCount(2);
        entries.Should().OnlyContain(e => !e.Message.Contains(secret, StringComparison.Ordinal));
    }

    [Fact]
    public void MeasuredDurationMs_UsesTheHarnessClock_OrDeclinesToGuess()
    {
        var entries = HarnessEvents.ToExecutionLogEntries(HarnessEvents.Parse(Journal), DateTimeOffset.UnixEpoch);
        var plain = new ExecutionLogEntry { Timestamp = DateTimeOffset.UnixEpoch, Level = ExecutionLogLevel.Info, Message = "x" };

        HarnessEvents.MeasuredDurationMs(entries[0], entries[1]).Should().Be(100);
        HarnessEvents.MeasuredDurationMs(entries[2], entries[3]).Should().BeNull("the text line has no measured clock");
        HarnessEvents.MeasuredDurationMs(entries[1], null).Should().BeNull();
        HarnessEvents.MeasuredDurationMs(plain, entries[1]).Should().BeNull();
        HarnessEvents.MeasuredDurationMs(entries[1], entries[0]).Should().Be(0, "clocks never run backwards in the view");
    }

    [Fact]
    public void WriteDevkitRun_ProducesV1RecordsThatParseBack()
    {
        var result = new LocalRunResult
        {
            ProcessId = "geometry.buffer",
            Status = ExecutionJobStatus.Succeeded,
            Artifacts = ["data:application/geo+json;base64,e30="],
            Logs =
            [
                new ExecutionLogEntry { Timestamp = DateTimeOffset.UnixEpoch, Level = ExecutionLogLevel.Info, Message = "start", Phase = "execute" },
                new ExecutionLogEntry { Timestamp = DateTimeOffset.UnixEpoch.AddMilliseconds(5), Level = ExecutionLogLevel.Warning, Message = "slow",
                    Metadata = new Dictionary<string, string> { ["gdal.command"] = "ogr2ogr <scratch>/a <scratch>/b" } },
            ],
            Elapsed = TimeSpan.FromMilliseconds(250),
        };
        var phases = new[]
        {
            new LocalRunPhase { Phase = "parse", Elapsed = TimeSpan.FromMilliseconds(10) },
            new LocalRunPhase { Phase = "buffer", PercentComplete = 60, Elapsed = TimeSpan.FromMilliseconds(120) },
        };
        using var writer = new StringWriter();

        HarnessEvents.WriteDevkitRun(writer, result, phases);
        var events = HarnessEvents.Parse(writer.ToString());

        events.Select(static e => e.Kind).Should().Equal("phase", "progress", "log", "log", "artifact", "terminal");
        events.Should().OnlyContain(e => e.Version == 1 && e.Attempt == "devkit");
        events.Select(static e => e.Sequence).Should().Equal(0, 1, 2, 3, 4, 5);
        events[1].Percent.Should().Be(60);
        events[1].ElapsedMs.Should().Be(120);
        events[3].Level.Should().Be("warning");
        events[5].Status.Should().Be("succeeded");
        events[5].ExitCode.Should().Be(0);
        events[5].ElapsedMs.Should().Be(250);
    }
}
