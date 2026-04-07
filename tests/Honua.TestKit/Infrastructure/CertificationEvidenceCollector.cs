// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Xunit.Sdk;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// xUnit-friendly accumulator for cross-client certification evidence. Records
/// CERT-* results during a test run and writes a single
/// <see cref="CertEnvelope"/> JSON envelope conforming to schema v1.0
/// (<c>docs/gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// The collector is intentionally generic — it does not reference any
/// protocol-specific client library. Failure classification works through a
/// single rule: when a recorded test body throws, exceptions of type
/// <see cref="XunitException"/> are tagged <c>[server-regression]</c> in the
/// resulting <see cref="CertResult.Notes"/>; every other exception type is
/// tagged <c>[client-incompat]</c>. This mirrors the convention that
/// xUnit/FluentAssertions assertions only fail after the client successfully
/// round-trips, while client-side parser/transport errors surface as native
/// client exceptions.
/// </para>
/// <para>
/// Tests that need finer control may call the explicit
/// <see cref="Pass(string, CertContext?, long?)"/>,
/// <see cref="Skip(string, string)"/>,
/// <see cref="NotApplicable(string, string)"/>, or
/// <see cref="Fail(string, string, string?, long?)"/> APIs directly.
/// </para>
/// <para>
/// The collector is not thread-safe; it is intended to be owned by a single
/// xUnit test class instance and flushed once during dispose.
/// </para>
/// </remarks>
public sealed class CertificationEvidenceCollector
{
    /// <summary>
    /// Notes prefix used when a recorded body throws an
    /// <see cref="XunitException"/> (or compatible assertion failure).
    /// </summary>
    public const string ServerRegressionPrefix = "[server-regression]";

    /// <summary>
    /// Notes prefix used when a recorded body throws any other exception.
    /// </summary>
    public const string ClientIncompatPrefix = "[client-incompat]";

    private readonly Dictionary<string, CertResult> _results = new(StringComparer.Ordinal);
    private readonly string _clientLane;
    private readonly string _protocol;
    private readonly string _environment;
    private readonly string _clientVersion;
    private readonly string _serverVersion;
    private readonly string _outputDirectory;
    private readonly string _runId;
    private readonly string _runDate;

    /// <summary>
    /// Initializes a new collector. The <paramref name="runTimestamp"/> is
    /// captured at construction so the envelope filename is stable for the
    /// test run.
    /// </summary>
    public CertificationEvidenceCollector(
        string clientLane,
        string protocol,
        string environment,
        string clientVersion,
        string serverVersion,
        string outputDirectory,
        DateTimeOffset? runTimestamp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientLane);
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        _clientLane = clientLane;
        _protocol = protocol;
        _environment = environment;
        _clientVersion = clientVersion;
        _serverVersion = serverVersion;
        _outputDirectory = outputDirectory;

        var ts = (runTimestamp ?? DateTimeOffset.UtcNow).ToUniversalTime();
        _runId = ts.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        _runDate = ts.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Stable run identifier (UTC compact timestamp) used in the envelope
    /// filename and the <see cref="CertEnvelope.RunId"/> field.
    /// </summary>
    public string RunId => _runId;

    /// <summary>
    /// Path of the envelope file that <see cref="Flush"/> will write.
    /// </summary>
    public string EnvelopePath =>
        Path.Combine(_outputDirectory, $"{_runId}-{_clientLane}-{_protocol}.cert.json");

    /// <summary>
    /// Number of results recorded so far.
    /// </summary>
    public int RecordCount => _results.Count;

    /// <summary>
    /// Executes <paramref name="body"/> and records a <c>pass</c> result if
    /// it returns normally. If it throws, classifies the failure as
    /// <c>server-regression</c> (xUnit assertion failure) or
    /// <c>client-incompat</c> (any other exception), records a
    /// <c>fail</c> result, and rethrows so xUnit still surfaces the failure
    /// in its normal channel.
    /// </summary>
    /// <param name="certId">CERT-* identifier from the certification matrix.</param>
    /// <param name="body">
    /// Asynchronous body that drives the client. The supplied
    /// <see cref="CertContext"/> lets the body attach metadata
    /// (e.g., <see cref="CertContext.MeasuredCount"/>, <see cref="CertContext.Notes"/>)
    /// that ends up on the recorded <see cref="CertResult"/>.
    /// </param>
    public async Task RecordAsync(string certId, Func<CertContext, Task> body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(certId);
        ArgumentNullException.ThrowIfNull(body);

        var ctx = new CertContext();
        var sw = Stopwatch.StartNew();
        try
        {
            await body(ctx).ConfigureAwait(false);
            sw.Stop();
            Upsert(new CertResult
            {
                TestCaseId = certId,
                Status = "pass",
                DurationMs = sw.ElapsedMilliseconds,
                MeasuredCount = ctx.MeasuredCount,
                MeasuredDelta = ctx.MeasuredDelta,
                Notes = ctx.Notes ?? string.Empty,
                EvidenceRef = ctx.EvidenceRef ?? string.Empty,
            });
        }
        catch (XunitException ex)
        {
            sw.Stop();
            Upsert(new CertResult
            {
                TestCaseId = certId,
                Status = "fail",
                DurationMs = sw.ElapsedMilliseconds,
                MeasuredCount = ctx.MeasuredCount,
                MeasuredDelta = ctx.MeasuredDelta,
                Notes = $"{ServerRegressionPrefix} {Truncate(ex.Message, 500)}",
                EvidenceRef = ctx.EvidenceRef ?? string.Empty,
            });
            throw new XunitException($"{ServerRegressionPrefix} {certId}: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Upsert(new CertResult
            {
                TestCaseId = certId,
                Status = "fail",
                DurationMs = sw.ElapsedMilliseconds,
                MeasuredCount = ctx.MeasuredCount,
                MeasuredDelta = ctx.MeasuredDelta,
                Notes = $"{ClientIncompatPrefix} {ex.GetType().Name}: {Truncate(ex.Message, 480)}",
                EvidenceRef = ctx.EvidenceRef ?? string.Empty,
            });
            throw new XunitException($"{ClientIncompatPrefix} {certId}: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Records an explicit <c>pass</c> for a CERT-* case. Use when the test
    /// body needs to bypass the auto-classification provided by
    /// <see cref="RecordAsync"/>.
    /// </summary>
    public void Pass(string certId, CertContext? ctx = null, long? durationMs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(certId);
        var c = ctx ?? new CertContext();
        Upsert(new CertResult
        {
            TestCaseId = certId,
            Status = "pass",
            DurationMs = durationMs,
            MeasuredCount = c.MeasuredCount,
            MeasuredDelta = c.MeasuredDelta,
            Notes = c.Notes ?? string.Empty,
            EvidenceRef = c.EvidenceRef ?? string.Empty,
        });
    }

    /// <summary>
    /// Records an explicit <c>skip</c> with a reason. The reason is
    /// surfaced in <see cref="CertResult.Notes"/> per the spec.
    /// </summary>
    public void Skip(string certId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(certId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Upsert(new CertResult
        {
            TestCaseId = certId,
            Status = "skip",
            Notes = reason,
        });
    }

    /// <summary>
    /// Records an explicit <c>not-applicable</c> with a reason. Use for
    /// CERT-* IDs that the lane does not exercise (e.g., geometry fidelity
    /// for the CLI/OData lane).
    /// </summary>
    public void NotApplicable(string certId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(certId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Upsert(new CertResult
        {
            TestCaseId = certId,
            Status = "not-applicable",
            Notes = reason,
        });
    }

    /// <summary>
    /// Records an explicit <c>fail</c> with a classification prefix. Tests
    /// that pre-classify failures via raw HTTP probes can call this directly.
    /// </summary>
    public void Fail(
        string certId,
        string classificationPrefix,
        string? message,
        long? durationMs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(certId);
        ArgumentException.ThrowIfNullOrWhiteSpace(classificationPrefix);
        var note = string.IsNullOrEmpty(message)
            ? classificationPrefix
            : $"{classificationPrefix} {Truncate(message, 500)}";
        Upsert(new CertResult
        {
            TestCaseId = certId,
            Status = "fail",
            DurationMs = durationMs,
            Notes = note,
        });
    }

    /// <summary>
    /// Serializes the accumulated results to the envelope file. Idempotent —
    /// multiple calls overwrite the same path with the latest snapshot.
    /// </summary>
    public string Flush()
    {
        var ordered = _results.Values
            .OrderBy(r => r.TestCaseId, StringComparer.Ordinal)
            .ToArray();

        var summary = new CertSummary
        {
            Total = ordered.Length,
            Passed = ordered.Count(r => r.Status == "pass"),
            Failed = ordered.Count(r => r.Status == "fail"),
            Skipped = ordered.Count(r => r.Status == "skip"),
            NotApplicable = ordered.Count(r => r.Status == "not-applicable"),
        };

        var envelope = new CertEnvelope
        {
            SchemaVersion = "1.0",
            RunId = _runId,
            RunDate = _runDate,
            ServerVersion = _serverVersion,
            ClientLane = _clientLane,
            ClientVersion = _clientVersion,
            Protocol = _protocol,
            Environment = _environment,
            Results = ordered,
            Summary = summary,
            CiteResults = null,
            Extensions = [],
        };

        Directory.CreateDirectory(_outputDirectory);
        var path = EnvelopePath;
        var json = JsonSerializer.Serialize(envelope, CertEnvelopeJsonContext.Default.CertEnvelope);
        File.WriteAllText(path, json);
        return path;
    }

    private void Upsert(CertResult result)
    {
        // Status precedence mirrors the PyQGIS collector: fail > pass > skip
        // = not-applicable. When statuses tie, prefer the record carrying
        // richer metadata (measured_count / notes / duration) so subsequent
        // bookkeeping calls cannot erase the test body's evidence.
        if (!_results.TryGetValue(result.TestCaseId, out var existing))
        {
            _results[result.TestCaseId] = result;
            return;
        }

        var newRank = Rank(result.Status);
        var existingRank = Rank(existing.Status);
        if (newRank > existingRank)
        {
            _results[result.TestCaseId] = result;
            return;
        }

        if (newRank < existingRank)
        {
            return;
        }

        if (Richness(result) > Richness(existing))
        {
            _results[result.TestCaseId] = result;
        }
    }

    private static int Rank(string status) => status switch
    {
        "fail" => 3,
        "pass" => 2,
        "skip" => 1,
        "not-applicable" => 1,
        _ => 0,
    };

    private static int Richness(CertResult r)
    {
        var score = 0;
        if (r.MeasuredCount.HasValue) score += 4;
        if (r.MeasuredDelta.HasValue) score += 2;
        if (!string.IsNullOrEmpty(r.EvidenceRef)) score += 2;
        if (!string.IsNullOrEmpty(r.Notes)) score += 1;
        if (r.DurationMs.HasValue) score += 1;
        return score;
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= max ? value : value[..max];
    }
}

/// <summary>
/// Mutable per-result context handed to a <see cref="CertificationEvidenceCollector.RecordAsync"/>
/// body. The recorded test attaches measured counts, deltas, notes, or
/// evidence references; the collector picks them up after the body completes.
/// </summary>
public sealed class CertContext
{
    /// <summary>Observed item count for count-based CERT cases.</summary>
    public long? MeasuredCount { get; set; }

    /// <summary>Maximum coordinate deviation for CERT-GEOM-01 style cases.</summary>
    public double? MeasuredDelta { get; set; }

    /// <summary>Free-text notes appended to <see cref="CertResult.Notes"/>.</summary>
    public string? Notes { get; set; }

    /// <summary>Optional path or URL to supporting evidence.</summary>
    public string? EvidenceRef { get; set; }
}
