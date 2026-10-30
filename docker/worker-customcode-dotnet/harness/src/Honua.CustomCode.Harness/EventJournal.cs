// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.CustomCode.Sdk;

namespace Honua.CustomCode.Harness;

/// <summary>
/// Append-only, bounded <c>honua.gp.events/v1</c> journal: the structured channel beside
/// the human-readable stderr lines. Progress, log, artifact, failure, and terminal
/// records share one JSON Lines contract across the production harnesses, the local
/// runners, and the devkit, so local and remote evidence have the same shape. Once the
/// event or byte cap is reached, further non-terminal records are counted and a single
/// <c>truncated</c> record discloses the count before the <c>terminal</c> record, which
/// is always written. Mirrors the Python harness's <c>events.py</c>.
/// </summary>
public sealed class EventJournal : IProgressReporter, IGpLogger, IDisposable
{
    /// <summary>The schema identifier recorded in receipts.</summary>
    public const string Schema = "honua.gp.events/v1";

    /// <summary>The <c>v</c> value every record carries.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The reserved artifact / default file name of the journal.</summary>
    public const string FileName = "honua-events.v1.jsonl";

    /// <summary>The environment variable that overrides the journal path.</summary>
    public const string PathEnvName = "CUSTOMCODE_EVENTS_PATH";

    /// <summary>Default record cap.</summary>
    public const int DefaultMaxEvents = 1000;

    /// <summary>Default byte cap.</summary>
    public const long DefaultMaxBytes = 256 * 1024;

    private const int MessageLimit = 2000;
    private const int PhaseLimit = 500;
    private static readonly string[] AttemptEnvNames = ["HONUA_JOB_ATTEMPT", "HONUA_ATTEMPT_ID", "AWS_BATCH_JOB_ATTEMPT"];
    private static readonly string[] TraceParentEnvNames = ["HONUA_TRACEPARENT", "TRACEPARENT"];
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    {
        "phase", "progress", "log", "artifact", "failure", "truncated", "terminal"
    };

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly IGpLogger? _mirror;

    /// <summary>Creates a journal at <paramref name="path"/> (parent directories are created).</summary>
    /// <param name="path">The JSON Lines file to append to.</param>
    /// <param name="attempt">The attempt identity recorded on every record.</param>
    /// <param name="maxEvents">Record cap; terminal records are exempt.</param>
    /// <param name="maxBytes">Byte cap; terminal records are exempt.</param>
    /// <param name="mirror">An optional logger that still receives the human-readable lines.</param>
    /// <param name="correlation">Admitted correlation id. Unsafe values are omitted.</param>
    /// <param name="trace">32-hex trace id. Other values are omitted.</param>
    public EventJournal(string path, string attempt = "1", int maxEvents = DefaultMaxEvents,
        long maxBytes = DefaultMaxBytes, IGpLogger? mirror = null, string? correlation = null, string? trace = null)
    {
        if (maxEvents <= 0 || maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEvents), "Event caps must be positive.");
        }

        Path = System.IO.Path.GetFullPath(path);
        Attempt = Limit(attempt, 64);
        Correlation = NormalizeCorrelation(correlation);
        Trace = NormalizeTraceId(trace);
        MaxEvents = maxEvents;
        MaxBytes = maxBytes;
        _mirror = mirror;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        using (File.Open(Path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            // Create the file eagerly so readers see an empty journal before the first record.
        }
    }

    /// <summary>The journal file.</summary>
    public string Path { get; }

    /// <summary>The attempt identity on every record.</summary>
    public string Attempt { get; }

    /// <summary>The correlation id on every record, when the run was given a safe one.</summary>
    public string? Correlation { get; }

    /// <summary>The 32-hex trace id on every record, when the run was given one.</summary>
    public string? Trace { get; }

    /// <summary>The record cap.</summary>
    public int MaxEvents { get; }

    /// <summary>The byte cap.</summary>
    public long MaxBytes { get; }

    /// <summary>Records written.</summary>
    public int Count { get; private set; }

    /// <summary>Bytes written.</summary>
    public long Bytes { get; private set; }

    /// <summary>Records dropped by a cap.</summary>
    public int Dropped { get; private set; }

    /// <summary>Whether any record was dropped.</summary>
    public bool Truncated => Dropped > 0;

    /// <summary>The attempt identity declared by the environment, or <c>1</c>.</summary>
    /// <param name="env">The job environment (the process environment when null).</param>
    public static string AttemptFromEnvironment(IReadOnlyDictionary<string, string?>? env)
    {
        foreach (var name in AttemptEnvNames)
        {
            var value = env is null ? Environment.GetEnvironmentVariable(name) : env.GetValueOrDefault(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return Limit(value, 64);
            }
        }

        return "1";
    }

    /// <summary>The correlation id declared by <c>HONUA_CORRELATION_ID</c>, or null.</summary>
    /// <param name="env">The job environment (the process environment when null).</param>
    public static string? CorrelationFromEnvironment(IReadOnlyDictionary<string, string?>? env)
        => NormalizeCorrelation(Read(env, "HONUA_CORRELATION_ID"));

    /// <summary>The trace id declared by <c>HONUA_TRACEPARENT</c> or <c>TRACEPARENT</c>, or null.</summary>
    /// <param name="env">The job environment (the process environment when null).</param>
    public static string? TraceFromEnvironment(IReadOnlyDictionary<string, string?>? env)
    {
        foreach (var name in TraceParentEnvNames)
        {
            if (TraceIdFromParent(Read(env, name)) is { } trace)
            {
                return trace;
            }
        }

        return null;
    }

    private static string? Read(IReadOnlyDictionary<string, string?>? env, string name)
        => env is null ? Environment.GetEnvironmentVariable(name) : env.GetValueOrDefault(name);

    /// <summary>The journal path declared by the environment, or <paramref name="fallback"/>.</summary>
    /// <param name="env">The job environment (the process environment when null).</param>
    /// <param name="fallback">The default path.</param>
    public static string PathFromEnvironment(IReadOnlyDictionary<string, string?>? env, string fallback)
    {
        var value = env is null ? Environment.GetEnvironmentVariable(PathEnvName) : env.GetValueOrDefault(PathEnvName);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    /// <summary>
    /// Reads a journal; lines that are not v1 records come back as
    /// <c>{"kind":"text","line":...}</c> so a tool that writes junk into the channel
    /// degrades to text instead of breaking the reader.
    /// </summary>
    /// <param name="path">The journal file.</param>
    public static IEnumerable<JsonObject> Read(string path)
    {
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonNode? node = null;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                // Fall through: treated as text below.
            }

            if (node is JsonObject record &&
                record["v"] is JsonValue version && version.TryGetValue<int>(out var v) && v == SchemaVersion &&
                record["kind"]?.GetValue<string>() is { } kind && Kinds.Contains(kind))
            {
                yield return record;
            }
            else
            {
                yield return new JsonObject { ["kind"] = "text", ["line"] = Limit(line, MessageLimit) };
            }
        }
    }

    /// <summary>Appends one record; returns false when a cap dropped it.</summary>
    /// <param name="kind">The record kind.</param>
    /// <param name="fields">Extra fields; null values are omitted.</param>
    /// <param name="terminal">Whether the record is exempt from the caps.</param>
    public bool Emit(string kind, JsonObject? fields = null, bool terminal = false)
    {
        if (!Kinds.Contains(kind))
        {
            throw new ArgumentException($"Unknown event kind '{kind}'.", nameof(kind));
        }

        lock (_gate)
        {
            var record = new JsonObject
            {
                ["v"] = SchemaVersion,
                ["seq"] = Count,
                ["at"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                ["elapsedMs"] = _clock.ElapsedMilliseconds,
                ["attempt"] = Attempt,
            };
            if (Correlation is not null)
            {
                record["correlation"] = Correlation;
            }

            if (Trace is not null)
            {
                record["trace"] = Trace;
            }

            record["kind"] = kind;
            if (fields is not null)
            {
                foreach (var (key, value) in fields.ToArray())
                {
                    if (value is not null)
                    {
                        fields.Remove(key);
                        record[key] = value;
                    }
                }
            }

            var encoded = Encoding.UTF8.GetBytes(record.ToJsonString() + "\n");
            if (!terminal && (Count >= MaxEvents || Bytes + encoded.Length > MaxBytes))
            {
                Dropped++;
                return false;
            }

            // Open per record: the tool or a sibling process may append to the same file, and
            // a long-lived handle would silently overwrite what they wrote.
            using (var stream = File.Open(Path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                stream.Write(encoded);
            }

            Count++;
            Bytes += encoded.Length;
            return true;
        }
    }

    /// <inheritdoc />
    public void Report(double percent, string phase)
    {
        if (!double.IsFinite(percent))
        {
            throw new ArgumentOutOfRangeException(nameof(percent));
        }

        var clamped = Math.Clamp(percent, 0.0, 100.0);
        Emit("progress", new JsonObject { ["percent"] = clamped, ["phase"] = Limit(phase, PhaseLimit) });
        _mirror?.Info($"progress {clamped.ToString("F1", CultureInfo.InvariantCulture),5}% — {phase}");
    }

    /// <inheritdoc />
    public void Info(string message)
    {
        Emit("log", new JsonObject { ["level"] = "info", ["message"] = Limit(message, MessageLimit) });
        _mirror?.Info(message);
    }

    /// <inheritdoc />
    public void Warn(string message)
    {
        Emit("log", new JsonObject { ["level"] = "warning", ["message"] = Limit(message, MessageLimit) });
        _mirror?.Warn(message);
    }

    /// <summary>Records the start of a harness phase.</summary>
    /// <param name="name">The phase name.</param>
    public void Phase(string name) => Emit("phase", new JsonObject { ["phase"] = Limit(name, PhaseLimit) });

    /// <summary>Records a published artifact.</summary>
    /// <param name="name">The artifact name.</param>
    /// <param name="sizeBytes">Its size.</param>
    /// <param name="sha256">Its digest, when known.</param>
    /// <param name="uri">Its upload destination, when known.</param>
    public void Artifact(string name, long sizeBytes, string? sha256 = null, string? uri = null)
    {
        var artifact = new JsonObject { ["name"] = Limit(name, 255), ["sizeBytes"] = sizeBytes };
        if (sha256 is not null)
        {
            artifact["sha256"] = sha256;
        }

        if (uri is not null)
        {
            artifact["uri"] = uri;
        }

        Emit("artifact", new JsonObject { ["artifact"] = artifact });
    }

    /// <summary>Records a structured failure.</summary>
    /// <param name="errorType">The error type name.</param>
    /// <param name="message">The error message.</param>
    /// <param name="phase">The phase that failed, when known.</param>
    public void Failure(string errorType, string message, string? phase = null)
        => Emit("failure", new JsonObject
        {
            ["phase"] = phase is null ? null : Limit(phase, PhaseLimit),
            ["error"] = new JsonObject { ["type"] = Limit(errorType, 200), ["message"] = Limit(message, MessageLimit) }
        });

    /// <summary>Always written: the truncation disclosure (when needed) and the terminal record.</summary>
    /// <param name="status">The terminal status.</param>
    /// <param name="exitCode">The process exit code.</param>
    /// <param name="phase">The phase the run stopped in, when it did not succeed.</param>
    public void Terminal(string status, int exitCode, string? phase = null)
    {
        if (Dropped > 0)
        {
            Emit("truncated", new JsonObject { ["dropped"] = Dropped, ["maxEvents"] = MaxEvents, ["maxBytes"] = MaxBytes }, terminal: true);
        }

        Emit("terminal", new JsonObject
        {
            ["status"] = Limit(status, 64),
            ["exitCode"] = exitCode,
            ["phase"] = phase is null ? null : Limit(phase, PhaseLimit)
        }, terminal: true);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing is held open between records; kept so callers can scope the journal with `using`.
    }

    private static string Limit(string value, int length) => value.Length > length ? value[..length] : value;

    private static string? NormalizeCorrelation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (text.Length is < 1 or > 128)
        {
            return null;
        }

        foreach (var character in text)
        {
            if (character is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or ':' or '-'))
            {
                return null;
            }
        }

        return text;
    }

    private static string? NormalizeTraceId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim().ToLowerInvariant();
        return text.Length == 32 && IsHex(text) && !IsAllZero(text) ? text : null;
    }

    private static string? TraceIdFromParent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Trim().ToLowerInvariant().Split('-');
        if (parts.Length != 4 || parts[0] != "00" || parts[1].Length != 32 || parts[2].Length != 16 || parts[3].Length != 2
            || !IsHex(parts[1]) || !IsHex(parts[2]) || !IsHex(parts[3]) || IsAllZero(parts[1]) || IsAllZero(parts[2]))
        {
            return null;
        }

        return parts[1];
    }

    private static bool IsHex(string value)
    {
        foreach (var character in value)
        {
            if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return value.Length > 0;
    }

    private static bool IsAllZero(string value)
    {
        foreach (var character in value)
        {
            if (character != '0')
            {
                return false;
            }
        }

        return true;
    }
}
