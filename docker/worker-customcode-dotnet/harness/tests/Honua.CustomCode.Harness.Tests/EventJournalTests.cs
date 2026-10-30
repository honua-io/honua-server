// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.CustomCode.Sdk;
using Xunit;

namespace Honua.CustomCode.Harness.Tests;

public sealed class EventJournalTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("gp-events-");

    private string JournalPath => Path.Join(_directory.FullName, "events.jsonl");

    private static List<JsonObject> Lines(string path)
        => File.ReadAllLines(path).Where(static l => l.Length > 0).Select(static l => (JsonObject)JsonNode.Parse(l)!).ToList();

    [Fact]
    public void Records_CarryVersionSequenceTimeAndAttempt()
    {
        using (var journal = new EventJournal(JournalPath, attempt: "7"))
        {
            journal.Phase("setup");
            journal.Report(42.5, "transform");
            journal.Info("hello");
            journal.Warn("careful");
            journal.Artifact("out.txt", 12, sha256: new string('a', 64));
            journal.Failure("ValueError", "bad", "execute");
            journal.Terminal("failed", 1, "execute");
        }

        var records = Lines(JournalPath);
        records.Select(static r => r["kind"]!.GetValue<string>()).Should()
            .Equal("phase", "progress", "log", "log", "artifact", "failure", "terminal");
        records.Select(static r => r["seq"]!.GetValue<int>()).Should().Equal(Enumerable.Range(0, 7));
        records.Should().OnlyContain(r => r["v"]!.GetValue<int>() == 1 && r["attempt"]!.GetValue<string>() == "7"
                                          && r["at"]!.GetValue<string>().EndsWith('Z'));
        records.Zip(records.Skip(1)).Should().OnlyContain(pair =>
            pair.First["elapsedMs"]!.GetValue<long>() <= pair.Second["elapsedMs"]!.GetValue<long>());
        records[1]["percent"]!.GetValue<double>().Should().Be(42.5);
        records[1]["phase"]!.GetValue<string>().Should().Be("transform");
        records[2]["level"]!.GetValue<string>().Should().Be("info");
        records[3]["level"]!.GetValue<string>().Should().Be("warning");
        records[4]["artifact"]!["name"]!.GetValue<string>().Should().Be("out.txt");
        records[4]["artifact"]!["sizeBytes"]!.GetValue<long>().Should().Be(12);
        records[5]["error"]!["type"]!.GetValue<string>().Should().Be("ValueError");
        records[6]["status"]!.GetValue<string>().Should().Be("failed");
        records[6]["exitCode"]!.GetValue<int>().Should().Be(1);
        records[6]["phase"]!.GetValue<string>().Should().Be("execute");
    }

    [Fact]
    public void Records_CarryCorrelationAndTraceWhenSupplied()
    {
        const string trace = "4bf92f3577b34da6a3ce929d0e0e4736";
        using (var journal = new EventJournal(JournalPath, correlation: "corr-admitted", trace: trace))
        {
            journal.Phase("execute");
            journal.Terminal("succeeded", 0);
        }

        var records = Lines(JournalPath);
        records.Should().OnlyContain(record =>
            record["correlation"]!.GetValue<string>() == "corr-admitted"
            && record["trace"]!.GetValue<string>() == trace);
    }

    [Fact]
    public void UnsafeCorrelationAndInvalidTrace_AreOmitted()
    {
        using (var journal = new EventJournal(JournalPath, correlation: "password=hunter2", trace: "not-a-trace"))
        {
            journal.Phase("execute");
        }

        var record = Lines(JournalPath)[0];
        record.ContainsKey("correlation").Should().BeFalse();
        record.ContainsKey("trace").Should().BeFalse();
        EventJournal.CorrelationFromEnvironment(new Dictionary<string, string?> { ["HONUA_CORRELATION_ID"] = " corr-1 " })
            .Should().Be("corr-1");
        EventJournal.TraceFromEnvironment(new Dictionary<string, string?>
        {
            ["HONUA_TRACEPARENT"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
        }).Should().Be("4bf92f3577b34da6a3ce929d0e0e4736");
        EventJournal.TraceFromEnvironment(new Dictionary<string, string?>
        {
            ["TRACEPARENT"] = "00-" + new string('0', 32) + "-" + new string('1', 16) + "-01"
        }).Should().BeNull();
    }

    [Fact]
    public void EventCap_DisclosesDroppedCount_AndKeepsTerminal()
    {
        using (var journal = new EventJournal(JournalPath, maxEvents: 5))
        {
            for (var i = 0; i < 9; i++)
            {
                journal.Info(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            journal.Terminal("succeeded", 0);
            journal.Truncated.Should().BeTrue();
            journal.Dropped.Should().Be(4);
        }

        var records = Lines(JournalPath);
        records.Should().HaveCount(7);
        records[5]["kind"]!.GetValue<string>().Should().Be("truncated");
        records[5]["dropped"]!.GetValue<int>().Should().Be(4);
        records[5]["maxEvents"]!.GetValue<int>().Should().Be(5);
        records[6]["kind"]!.GetValue<string>().Should().Be("terminal");
        records[6]["seq"]!.GetValue<int>().Should().Be(6);
    }

    [Fact]
    public void ByteCap_CountsOversizedRecordsAsDropped()
    {
        using (var journal = new EventJournal(JournalPath, maxBytes: 400))
        {
            for (var i = 0; i < 50; i++)
            {
                journal.Info(new string('x', 100));
            }

            journal.Terminal("succeeded", 0);
        }

        var records = Lines(JournalPath);
        records[^1]["kind"]!.GetValue<string>().Should().Be("terminal");
        records[^2]["kind"]!.GetValue<string>().Should().Be("truncated");
        (records.Count - 2).Should().BeInRange(1, 49);
        records[^2]["dropped"]!.GetValue<int>().Should().Be(50 - (records.Count - 2));
    }

    [Fact]
    public void Read_TreatsMalformedAndForeignLinesAsText()
    {
        using (var journal = new EventJournal(JournalPath))
        {
            journal.Phase("setup");
            File.AppendAllText(JournalPath, "not json at all\n{\"v\": 99, \"kind\": \"phase\"}\n{\"kind\": \"progress\"}\n\n");
            journal.Terminal("succeeded", 0);
        }

        var kinds = EventJournal.Read(JournalPath).Select(static r => r["kind"]!.GetValue<string>()).ToList();
        kinds.Should().Equal("phase", "text", "text", "text", "terminal");
        EventJournal.Read(JournalPath).ElementAt(1)["line"]!.GetValue<string>().Should().Be("not json at all");
    }

    [Fact]
    public void ConcurrentEmitters_KeepSequenceContiguous()
    {
        using (var journal = new EventJournal(JournalPath, maxEvents: 300))
        {
            Parallel.For(0, 500, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
            {
                journal.Report(50, "working");
                journal.Info(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            });
            journal.Terminal("succeeded", 0);
        }

        var records = Lines(JournalPath);
        records.Select(static r => r["seq"]!.GetValue<int>()).Should().Equal(Enumerable.Range(0, records.Count));
        records.Should().HaveCount(302);
        records[^2]["dropped"]!.GetValue<int>().Should().Be(700);
    }

    [Fact]
    public void Mirror_KeepsTheStderrTextUnchanged()
    {
        using var writer = new StringWriter();
        using (var journal = new EventJournal(JournalPath, mirror: new HarnessLogger(writer)))
        {
            journal.Report(50, "transform");
            journal.Info("hello");
            journal.Warn("careful");
        }

        writer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal)
            .Should().Be("[tool] INFO  progress  50.0% — transform\n[tool] INFO  hello\n[tool] WARN  careful\n");
    }

    [Fact]
    public void NonFiniteProgress_AndUnknownKind_AreRejected()
    {
        using var journal = new EventJournal(JournalPath);
        journal.Invoking(j => j.Report(double.NaN, "x")).Should().Throw<ArgumentOutOfRangeException>();
        journal.Invoking(j => j.Emit("bogus")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Environment_ResolvesAttemptAndPath()
    {
        var env = new Dictionary<string, string?> { ["HONUA_ATTEMPT_ID"] = "a1", ["AWS_BATCH_JOB_ATTEMPT"] = "3" };
        EventJournal.AttemptFromEnvironment(env).Should().Be("a1");
        EventJournal.AttemptFromEnvironment(new Dictionary<string, string?> { ["AWS_BATCH_JOB_ATTEMPT"] = "3" }).Should().Be("3");
        EventJournal.AttemptFromEnvironment(new Dictionary<string, string?>()).Should().Be("1");
        EventJournal.PathFromEnvironment(new Dictionary<string, string?> { [EventJournal.PathEnvName] = "/x/ev.jsonl" }, "/d").Should().Be("/x/ev.jsonl");
        EventJournal.PathFromEnvironment(new Dictionary<string, string?>(), "/d").Should().Be("/d");
    }

    public void Dispose() => _directory.Delete(recursive: true);
}
