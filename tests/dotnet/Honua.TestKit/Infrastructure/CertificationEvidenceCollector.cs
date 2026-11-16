// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Xunit.Sdk;

namespace Honua.TestKit.Infrastructure;

public sealed class CertificationEvidenceCollector
{
    public const string ServerRegressionPrefix = "[server-regression]";

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

    public string RunId => _runId;

    // The second argument is always a generated file name (never rooted), so this
    // combine can't drop _outputDirectory.
    public string EnvelopePath =>
        Path.Join(_outputDirectory, $"{_runId}-{_clientLane}-{_protocol}.cert.json");

    public int RecordCount => _results.Count;

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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Broad catch is intentional: any non-assertion exception is classified as a
            // client-incompat failure, recorded, then rethrown as XunitException so xUnit
            // still surfaces it via its normal failure channel.
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

public sealed class CertContext
{
    public long? MeasuredCount { get; set; }

    public double? MeasuredDelta { get; set; }

    public string? Notes { get; set; }

    public string? EvidenceRef { get; set; }
}
