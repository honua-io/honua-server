// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing.LocalRunner;

namespace Honua.Geoprocessing.CustomCode;

/// <summary>
/// One <c>honua.gp.events/v1</c> record as written by the custom-code harnesses, the
/// local runners, and the devkit (<c>docs/reference/gp-harness-events.v1.md</c>). A line
/// that is not a v1 record parses as <see cref="Kind"/> <c>text</c> with the raw line in
/// <see cref="Text"/>, so a tool that writes junk into the channel degrades to text.
/// </summary>
public sealed record HarnessEvent
{
    /// <summary>The schema version (<c>1</c>), or <c>0</c> for a text line.</summary>
    public int Version { get; init; }

    /// <summary>The journal sequence number.</summary>
    public long Sequence { get; init; }

    /// <summary>The wall-clock time the record was written, when parseable.</summary>
    public DateTimeOffset? At { get; init; }

    /// <summary>Milliseconds since the harness started its monotonic clock.</summary>
    public long? ElapsedMs { get; init; }

    /// <summary>The attempt identity.</summary>
    public string Attempt { get; init; } = "1";

    /// <summary>phase, progress, log, artifact, failure, truncated, terminal, or text.</summary>
    public required string Kind { get; init; }

    /// <summary>The phase label, when the record carries one.</summary>
    public string? Phase { get; init; }

    /// <summary>Progress percent for <c>progress</c> records.</summary>
    public double? Percent { get; init; }

    /// <summary>info or warning for <c>log</c> records.</summary>
    public string? Level { get; init; }

    /// <summary>The log message.</summary>
    public string? Message { get; init; }

    /// <summary>The artifact name for <c>artifact</c> records.</summary>
    public string? ArtifactName { get; init; }

    /// <summary>The artifact size for <c>artifact</c> records.</summary>
    public long? ArtifactSizeBytes { get; init; }

    /// <summary>The artifact digest, when recorded.</summary>
    public string? ArtifactSha256 { get; init; }

    /// <summary>The artifact destination, when recorded.</summary>
    public string? ArtifactUri { get; init; }

    /// <summary>The error type for <c>failure</c> records.</summary>
    public string? ErrorType { get; init; }

    /// <summary>The error message for <c>failure</c> records.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>The terminal status for <c>terminal</c> records.</summary>
    public string? Status { get; init; }

    /// <summary>The process exit code for <c>terminal</c> records.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Records dropped by the journal cap, for <c>truncated</c> records.</summary>
    public int? Dropped { get; init; }

    /// <summary>The raw line for <c>text</c> records.</summary>
    public string? Text { get; init; }
}

/// <summary>
/// The server half of the <c>honua.gp.events/v1</c> contract: parses a harness journal,
/// forwards it as bounded <see cref="ExecutionLogEntry"/> records (overflow becomes one
/// truncation entry, never a silent drop), reads measured durations back out of the
/// forwarded metadata so the step view can label them, and writes the devkit's local
/// run evidence in the same shape.
/// </summary>
public static class HarnessEvents
{
    /// <summary>The schema identifier.</summary>
    public const string Schema = "honua.gp.events/v1";

    /// <summary>The <c>v</c> value every record carries.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The reserved artifact name the harnesses upload beside the tool's artifacts.</summary>
    public const string FileName = "honua-events.v1.jsonl";

    /// <summary>Metadata key: the schema version on a forwarded entry.</summary>
    public const string VersionKey = "honua.event.v";

    /// <summary>Metadata key: the record kind on a forwarded entry.</summary>
    public const string KindKey = "honua.event.kind";

    /// <summary>Metadata key: the journal sequence on a forwarded entry.</summary>
    public const string SequenceKey = "honua.event.seq";

    /// <summary>Metadata key: the attempt on a forwarded entry.</summary>
    public const string AttemptKey = "honua.event.attempt";

    /// <summary>Metadata key: the harness-measured elapsed milliseconds on a forwarded entry.</summary>
    public const string ElapsedKey = "honua.event.elapsedMs";

    /// <summary>Metadata key: progress percent on a forwarded entry.</summary>
    public const string PercentKey = "honua.event.percent";

    /// <summary>Metadata key: dropped-record count on a truncation entry.</summary>
    public const string DroppedKey = "honua.event.dropped";

    /// <summary>Default forwarded-entry cap per job.</summary>
    public const int DefaultMaxEntries = 1000;

    /// <summary>Default forwarded-bytes cap per job (messages plus metadata values).</summary>
    public const long DefaultMaxBytes = 256 * 1024;

    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    {
        "phase", "progress", "log", "artifact", "failure", "truncated", "terminal"
    };

    /// <summary>Parses journal text; every non-empty line yields one event.</summary>
    /// <param name="text">The journal contents.</param>
    public static IReadOnlyList<HarnessEvent> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Parse(text.Split('\n'));
    }

    /// <summary>Parses journal lines; every non-empty line yields one event.</summary>
    /// <param name="lines">The journal lines.</param>
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

    /// <summary>
    /// Maps harness events to job log entries under a count and byte cap. Messages pass
    /// through <see cref="WorkspacePathSanitizer.SanitizeForClient"/> so quoted, truncated,
    /// and escaped credentials are redacted before they reach the store. When the cap is
    /// hit, one warning entry discloses how many events were not forwarded; the
    /// <c>terminal</c> record is forwarded regardless.
    /// </summary>
    /// <param name="events">The parsed events, in journal order.</param>
    /// <param name="fallbackTimestamp">Timestamp for records without a parseable <c>at</c>.</param>
    /// <param name="maxEntries">Entry cap.</param>
    /// <param name="maxBytes">Byte cap over messages and metadata values.</param>
    public static IReadOnlyList<ExecutionLogEntry> ToExecutionLogEntries(
        IReadOnlyList<HarnessEvent> events,
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

    /// <summary>
    /// The harness-measured duration between a forwarded event and the next one, or
    /// <c>null</c> when either side lacks the measured clock (the caller then infers
    /// from log timestamps and must say so).
    /// </summary>
    /// <param name="entry">The entry whose duration is wanted.</param>
    /// <param name="next">The following entry, when there is one.</param>
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

    /// <summary>
    /// Writes a devkit run as v1 JSON Lines: the phase timeline, the executor's log
    /// entries, the published artifacts, and the terminal record, so a local
    /// <c>honua-gp-devkit run</c> leaves the same evidence shape as a remote job.
    /// </summary>
    /// <param name="writer">The destination.</param>
    /// <param name="result">The run outcome.</param>
    /// <param name="phases">The phase timeline reported during the run.</param>
    /// <param name="attempt">The attempt label (<c>devkit</c> by default).</param>
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
