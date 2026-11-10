// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing.LocalRunner;

namespace Honua.Geoprocessing.CustomCode;

public sealed record HarnessEvent
{
    public int Version { get; init; }

    public long Sequence { get; init; }

    public DateTimeOffset? At { get; init; }

    public long? ElapsedMs { get; init; }

    public string Attempt { get; init; } = "1";

    public string? Correlation { get; init; }

    public string? Trace { get; init; }

    public required string Kind { get; init; }

    public string? Phase { get; init; }

    public double? Percent { get; init; }

    public string? Level { get; init; }

    public string? Message { get; init; }

    public string? ArtifactName { get; init; }

    public long? ArtifactSizeBytes { get; init; }

    public string? ArtifactSha256 { get; init; }

    public string? ArtifactUri { get; init; }

    public string? ErrorType { get; init; }

    public string? ErrorMessage { get; init; }

    public string? Status { get; init; }

    public int? ExitCode { get; init; }

    public int? Dropped { get; init; }

    public string? Text { get; init; }
}

// Journal overflow becomes one truncation entry and is never a silent drop.
public static class HarnessEvents
{
    public const string Schema = "honua.gp.events/v1";

    public const int SchemaVersion = 1;

    public const string FileName = "honua-events.v1.jsonl";

    public const string VersionKey = "honua.event.v";

    public const string KindKey = "honua.event.kind";

    public const string SequenceKey = "honua.event.seq";

    public const string AttemptKey = "honua.event.attempt";

    public const string ElapsedKey = "honua.event.elapsedMs";

    public const string PercentKey = "honua.event.percent";

    public const string DroppedKey = "honua.event.dropped";

    public const int DefaultMaxEntries = 1000;

    public const long DefaultMaxBytes = 256 * 1024;

    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    {
        "phase", "progress", "log", "artifact", "failure", "truncated", "terminal"
    };

    public static IReadOnlyList<HarnessEvent> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Parse(text.Split('\n'));
    }

    public static IReadOnlyList<HarnessEvent> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var events = new List<HarnessEvent>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            events.Add(ParseLine(line));
        }

        return events;
    }

    private static HarnessEvent ParseLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var v) || v != SchemaVersion ||
                !root.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String ||
                kindElement.GetString() is not { } kind || !Kinds.Contains(kind))
            {
                return Text(line);
            }

            var artifact = root.TryGetProperty("artifact", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
            var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object ? e : default;
            return new HarnessEvent
            {
                Version = v,
                Kind = kind,
                Sequence = Int64(root, "seq") ?? 0,
                At = root.TryGetProperty("at", out var at) && at.ValueKind == JsonValueKind.String &&
                     DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedAt)
                    ? parsedAt
                    : null,
                ElapsedMs = Int64(root, "elapsedMs"),
                Attempt = String(root, "attempt") ?? "1",
                Correlation = String(root, "correlation"),
                Trace = String(root, "trace"),
                Phase = String(root, "phase"),
                Percent = Double(root, "percent"),
                Level = String(root, "level"),
                Message = String(root, "message"),
                ArtifactName = artifact.ValueKind == JsonValueKind.Object ? String(artifact, "name") : null,
                ArtifactSizeBytes = artifact.ValueKind == JsonValueKind.Object ? Int64(artifact, "sizeBytes") : null,
                ArtifactSha256 = artifact.ValueKind == JsonValueKind.Object ? String(artifact, "sha256") : null,
                ArtifactUri = artifact.ValueKind == JsonValueKind.Object ? String(artifact, "uri") : null,
                ErrorType = error.ValueKind == JsonValueKind.Object ? String(error, "type") : null,
                ErrorMessage = error.ValueKind == JsonValueKind.Object ? String(error, "message") : null,
                Status = String(root, "status"),
                ExitCode = (int?)Int64(root, "exitCode"),
                Dropped = (int?)Int64(root, "dropped"),
            };
        }
        catch (JsonException)
        {
            return Text(line);
        }
    }

    private static HarnessEvent Text(string line) => new() { Kind = "text", Text = line.Length > 2000 ? line[..2000] : line };

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Int64(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    private static double? Double(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;

    // Messages are redacted before they are stored, and the terminal record is forwarded even after the cap.
    public static IReadOnlyList<ExecutionLogEntry> ToExecutionLogEntries(
        IReadOnlyList<HarnessEvent> events,
        DateTimeOffset fallbackTimestamp,
        int maxEntries = DefaultMaxEntries,
        long maxBytes = DefaultMaxBytes)
        => ProjectExecutionLogEntries(events, fallbackTimestamp, maxEntries, maxBytes);

    internal static IReadOnlyList<ExecutionLogEntry> ProjectExecutionLogEntries(
        IEnumerable<HarnessEvent> events,
        DateTimeOffset fallbackTimestamp,
        int maxEntries = DefaultMaxEntries,
        long maxBytes = DefaultMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxEntries, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxBytes, 0);

        var entries = new List<ExecutionLogEntry>();
        long bytes = 0;
        var dropped = 0;
        ExecutionLogEntry? terminal = null;
        foreach (var e in events)
        {
            var entry = ToEntry(e, fallbackTimestamp);
            if (string.Equals(e.Kind, "terminal", StringComparison.Ordinal))
            {
                terminal = entry;
                continue;
            }

            var size = Size(entry);
            if (entries.Count >= maxEntries || bytes + size > maxBytes)
            {
                dropped++;
                continue;
            }

            entries.Add(entry);
            bytes += size;
        }

        if (dropped > 0)
        {
            entries.Add(new ExecutionLogEntry
            {
                Timestamp = terminal?.Timestamp ?? fallbackTimestamp,
                Level = ExecutionLogLevel.Warning,
                Phase = null,
                Message = $"{dropped} harness event(s) were not forwarded: the job-event cap ({maxEntries} entries, {maxBytes} bytes) was reached.",
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [VersionKey] = SchemaVersion.ToString(CultureInfo.InvariantCulture),
                    [KindKey] = "truncated",
                    [DroppedKey] = dropped.ToString(CultureInfo.InvariantCulture),
                },
            });
        }

        if (terminal is not null)
        {
            entries.Add(terminal);
        }

        return entries;
    }

    private static long Size(ExecutionLogEntry entry)
    {
        long size = entry.Message.Length + (entry.Phase?.Length ?? 0);
        if (entry.Metadata is not null)
        {
            foreach (var (key, value) in entry.Metadata)
            {
                size += key.Length + value.Length;
            }
        }

        return size;
    }

    private static ExecutionLogEntry ToEntry(HarnessEvent e, DateTimeOffset fallbackTimestamp)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [VersionKey] = e.Version.ToString(CultureInfo.InvariantCulture),
            [KindKey] = e.Kind,
            [SequenceKey] = e.Sequence.ToString(CultureInfo.InvariantCulture),
            [AttemptKey] = e.Attempt,
        };
        if (e.ElapsedMs is { } elapsed)
        {
            metadata[ElapsedKey] = elapsed.ToString(CultureInfo.InvariantCulture);
        }

        if (e.Percent is { } percent)
        {
            metadata[PercentKey] = percent.ToString("F1", CultureInfo.InvariantCulture);
        }

        var level = ExecutionLogLevel.Info;
        string message;
        switch (e.Kind)
        {
            case "phase":
                message = $"phase {e.Phase}";
                break;
            case "progress":
                message = $"progress {e.Percent?.ToString("F1", CultureInfo.InvariantCulture) ?? "?"}% — {e.Phase}";
                break;
            case "log":
                level = string.Equals(e.Level, "warning", StringComparison.OrdinalIgnoreCase) ? ExecutionLogLevel.Warning
                    : string.Equals(e.Level, "error", StringComparison.OrdinalIgnoreCase) ? ExecutionLogLevel.Error
                    : ExecutionLogLevel.Info;
                message = e.Message ?? string.Empty;
                break;
            case "artifact":
                message = $"artifact {e.ArtifactName} ({e.ArtifactSizeBytes?.ToString(CultureInfo.InvariantCulture) ?? "?"} bytes)";
                if (e.ArtifactName is not null)
                {
                    metadata["honua.event.artifact"] = e.ArtifactName;
                }

                if (e.ArtifactSha256 is not null)
                {
                    metadata["honua.event.sha256"] = e.ArtifactSha256;
                }

                break;
            case "failure":
                level = ExecutionLogLevel.Error;
                message = $"{e.ErrorType ?? "error"}: {e.ErrorMessage}";
                if (e.ErrorType is not null)
                {
                    metadata["honua.event.errorType"] = e.ErrorType;
                }

                break;
            case "truncated":
                level = ExecutionLogLevel.Warning;
                message = $"{e.Dropped?.ToString(CultureInfo.InvariantCulture) ?? "?"} harness event(s) were dropped by the journal cap";
                if (e.Dropped is { } d)
                {
                    metadata[DroppedKey] = d.ToString(CultureInfo.InvariantCulture);
                }

                break;
            case "terminal":
                level = string.Equals(e.Status, "succeeded", StringComparison.Ordinal) ? ExecutionLogLevel.Info : ExecutionLogLevel.Error;
                message = $"{e.Status} (exit {e.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"})";
                if (e.Status is not null)
                {
                    metadata["honua.event.status"] = e.Status;
                }

                if (e.ExitCode is { } code)
                {
                    metadata["honua.event.exitCode"] = code.ToString(CultureInfo.InvariantCulture);
                }

                break;
            default:
                message = e.Text ?? string.Empty;
                break;
        }

        return new ExecutionLogEntry
        {
            Timestamp = e.At ?? fallbackTimestamp,
            Level = level,
            Phase = e.Phase is null ? null : WorkspacePathSanitizer.SanitizeForClient(e.Phase, null),
            Message = WorkspacePathSanitizer.SanitizeForClient(message, null),
            Metadata = metadata,
        };
    }

    public static long? MeasuredDurationMs(ExecutionLogEntry entry, ExecutionLogEntry? next)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (next is null || !TryElapsed(entry, out var start) || !TryElapsed(next, out var end))
        {
            return null;
        }

        return Math.Max(0, end - start);
    }

    private static bool TryElapsed(ExecutionLogEntry entry, out long elapsed)
    {
        elapsed = 0;
        return entry.Metadata is not null &&
               entry.Metadata.TryGetValue(ElapsedKey, out var raw) &&
               long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out elapsed);
    }

    public static void WriteDevkitRun(TextWriter writer, LocalRunResult result, IReadOnlyList<LocalRunPhase> phases, string attempt = "devkit")
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(phases);

        var now = DateTimeOffset.UtcNow;
        var start = now - result.Elapsed;
        var sequence = 0L;
        var buffer = new MemoryStream();

        foreach (var phase in phases)
        {
            WriteRecord(writer, buffer, sequence++, start + phase.Elapsed, (long)phase.Elapsed.TotalMilliseconds, attempt,
                phase.PercentComplete is null ? "phase" : "progress", w =>
                {
                    w.WriteString("phase", phase.Phase);
                    if (phase.PercentComplete is { } percent)
                    {
                        w.WriteNumber("percent", Math.Clamp(percent, 0, 100));
                    }
                });
        }

        var firstLog = result.Logs.Count == 0 ? start : result.Logs.Min(static l => l.Timestamp);
        foreach (var log in result.Logs)
        {
            var elapsed = (long)Math.Max(0, (log.Timestamp - firstLog).TotalMilliseconds);
            var kind = log.Level == ExecutionLogLevel.Error ? "failure" : "log";
            WriteRecord(writer, buffer, sequence++, log.Timestamp, elapsed, attempt, kind, w =>
            {
                if (log.Phase is not null)
                {
                    w.WriteString("phase", log.Phase);
                }

                if (kind == "failure")
                {
                    w.WriteStartObject("error");
                    w.WriteString("type", "ExecutorError");
                    w.WriteString("message", log.Message);
                    w.WriteEndObject();
                }
                else
                {
                    w.WriteString("level", log.Level == ExecutionLogLevel.Warning ? "warning" : "info");
                    w.WriteString("message", log.Message);
                }

                if (log.Metadata is not null && log.Metadata.TryGetValue("gdal.command", out var command))
                {
                    w.WriteString("command", command);
                }
            });
        }

        for (var i = 0; i < result.Artifacts.Count; i++)
        {
            var reference = result.Artifacts[i];
            var index = i;
            WriteRecord(writer, buffer, sequence++, now, (long)result.Elapsed.TotalMilliseconds, attempt, "artifact", w =>
            {
                w.WriteStartObject("artifact");
                w.WriteString("name", $"artifact-{index}");
                w.WriteNumber("sizeBytes", reference.Length);
                w.WriteEndObject();
            });
        }

        var status = result.Succeeded ? "succeeded" : result.Status == ExecutionJobStatus.Cancelled ? "cancelled" : "failed";
        WriteRecord(writer, buffer, sequence, now, (long)result.Elapsed.TotalMilliseconds, attempt, "terminal", w =>
        {
            w.WriteString("status", status);
            w.WriteNumber("exitCode", result.Succeeded ? 0 : 1);
            if (!result.Succeeded && result.ErrorMessage is not null)
            {
                w.WriteString("message", result.ErrorMessage);
            }
        });
    }

    private static void WriteRecord(TextWriter writer, MemoryStream buffer, long sequence, DateTimeOffset at, long elapsedMs,
        string attempt, string kind, Action<Utf8JsonWriter> fields)
    {
        buffer.SetLength(0);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("v", SchemaVersion);
            json.WriteNumber("seq", sequence);
            json.WriteString("at", at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            json.WriteNumber("elapsedMs", elapsedMs);
            json.WriteString("attempt", attempt);
            json.WriteString("kind", kind);
            fields(json);
            json.WriteEndObject();
        }

        writer.Write(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
        writer.Write('\n');
    }
}
