// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Reporting;
using Honua.Core.Features.Reporting.Abstractions;
using Honua.Core.Features.Reporting.Domain;
using Honua.Geoprocessing;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Reporting;

/// <summary>
/// Orchestrates analysis report retrieval. Authorizes the caller through
/// <see cref="IGeoprocessingJobService"/>, builds the report once per
/// (jobId, contractVersion, resultPackageId), and caches rendered Markdown /
/// HTML output for the configured TTL.
/// </summary>
internal sealed class AnalysisReportService : IAnalysisReportService
{
    public static readonly ActivitySource ActivitySource = new("Honua.Server.Reporting");

    private static readonly Counter<long> _renderCount = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.reporting.render.count",
        unit: "{render}",
        description: "Count of analysis-report renders by format and narrative mode.");

    private static readonly Counter<long> _renderFailureCount = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.reporting.render.failure.count",
        unit: "{failure}",
        description: "Count of analysis-report render failures by reason.");

    private static readonly Counter<long> _cacheHitCount = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.reporting.cache.hit.count",
        unit: "{hit}",
        description: "Count of cached analysis-report body hits.");

    private static readonly Counter<long> _cacheMissCount = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.reporting.cache.miss.count",
        unit: "{miss}",
        description: "Count of cache misses requiring a render.");

    private static readonly Counter<long> _narrativeFallbackCount = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.reporting.narrative.fallback.count",
        unit: "{fallback}",
        description: "Count of narrative fallbacks from llm-error or deterministic-only.");

    private readonly IGeoprocessingJobService _jobService;
    private readonly IAnalysisReportBuilder _builder;
    private readonly IAnalysisReportStore _store;
    private readonly Dictionary<AnalysisReportFormat, IAnalysisReportRenderer> _renderers;
    private readonly IMemoryCache _cache;
    private readonly ReportingConfiguration _configuration;
    private readonly ILogger<AnalysisReportService> _logger;

    public AnalysisReportService(
        IGeoprocessingJobService jobService,
        IAnalysisReportBuilder builder,
        IAnalysisReportStore store,
        IEnumerable<IAnalysisReportRenderer> renderers,
        IMemoryCache cache,
        IOptions<ReportingConfiguration> configuration,
        ILogger<AnalysisReportService> logger)
    {
        _jobService = jobService;
        _builder = builder;
        _store = store;
        _cache = cache;
        _configuration = configuration.Value;
        _logger = logger;
        _renderers = renderers.ToDictionary(r => r.Format);
    }

    /// <inheritdoc />
    public async Task<AnalysisReport> GetReportAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentNullException.ThrowIfNull(principal);

        using var activity = ActivitySource.StartActivity("Reporting.GetReport");
        activity?.SetTag("report.job_id", jobId);
        activity?.SetTag("report.contract_version", ReportingConstants.ContractVersionV1);

        await _jobService
            .EnsureCallerAuthorizedAsync(principal, OperatorResourceType.Job, OperatorOperation.Read, cancellationToken)
            .ConfigureAwait(false);

        var package = await _jobService
            .GetJobResultsAsync(jobId, principal, cancellationToken)
            .ConfigureAwait(false);

        var cached = await _store
            .TryGetAsync(jobId, ReportingConstants.ContractVersionV1, cancellationToken)
            .ConfigureAwait(false);
        if (cached is not null && string.Equals(cached.ResultPackageId, package.ResultPackageId, StringComparison.Ordinal))
        {
            activity?.SetTag(ReportingConstants.NarrativeModeTag, NarrativeModeTag(cached.NarrativeMode));
            activity?.SetTag("report.template_id", cached.TemplateId);
            return cached;
        }

        if (cached is not null)
        {
            await _store.RemoveAsync(jobId, cancellationToken).ConfigureAwait(false);
        }

        var report = await _builder
            .BuildAsync(jobId, package, cancellationToken)
            .ConfigureAwait(false);

        await _store.StoreAsync(report, cancellationToken).ConfigureAwait(false);

        if (report.NarrativeMode != NarrativeMode.LlmAssisted)
        {
            _narrativeFallbackCount.Add(1,
                new KeyValuePair<string, object?>(ReportingConstants.NarrativeModeTag, NarrativeModeTag(report.NarrativeMode)));
        }

        activity?.SetTag(ReportingConstants.NarrativeModeTag, NarrativeModeTag(report.NarrativeMode));
        activity?.SetTag("report.template_id", report.TemplateId);
        activity?.SetTag("report.template_version", report.TemplateVersion);
        activity?.SetTag("report.section_count", report.Sections.Count);

        return report;
    }

    /// <inheritdoc />
    public async Task<RenderedAnalysisReport> GetRenderedAsync(
        string jobId,
        AnalysisReportFormat format,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        if (!_renderers.TryGetValue(format, out var renderer))
        {
            throw new InvalidOperationException($"No renderer is registered for format '{format}'.");
        }

        var report = await GetReportAsync(jobId, principal, cancellationToken).ConfigureAwait(false);
        var cacheKey = BuildCacheKey(report.JobId, format, report.ResultPackageId);

        if (_cache.TryGetValue<string>(cacheKey, out var cachedBody) && cachedBody is not null)
        {
            _cacheHitCount.Add(1, new KeyValuePair<string, object?>("format", format.ToString()));
            return new RenderedAnalysisReport(report, format, cachedBody);
        }

        _cacheMissCount.Add(1, new KeyValuePair<string, object?>("format", format.ToString()));
        string body;
        try
        {
            body = renderer.Render(report);
        }
        catch (UnsupportedReportContractVersionException)
        {
            _renderFailureCount.Add(1, new KeyValuePair<string, object?>("reason", "contract-unsupported"));
            throw;
        }
        catch (Exception)
        {
            _renderFailureCount.Add(1, new KeyValuePair<string, object?>("reason", "renderer-error"));
            throw;
        }

        var ttl = ResolveCacheTtl();
        _cache.Set(cacheKey, body, ttl);

        _renderCount.Add(1,
            new KeyValuePair<string, object?>("format", format.ToString()),
            new KeyValuePair<string, object?>(ReportingConstants.NarrativeModeTag, NarrativeModeTag(report.NarrativeMode)));
        AnalysisReportServerLog.ReportRendered(_logger, report.ReportId, report.TemplateId, format.ToString(), NarrativeModeTag(report.NarrativeMode));

        return new RenderedAnalysisReport(report, format, body);
    }

    private TimeSpan ResolveCacheTtl() => TimeSpan.FromMinutes(Math.Max(1, _configuration.Cache.TtlMinutes));

    private static string BuildCacheKey(string jobId, AnalysisReportFormat format, string resultPackageId)
        => string.Concat("report:", jobId, ":", ReportingConstants.ContractVersionV1, ":", format, ":", resultPackageId);

    private static string NarrativeModeTag(NarrativeMode mode) => mode switch
    {
        NarrativeMode.LlmAssisted => ReportingConstants.NarrativeModeLlmAssistedTag,
        NarrativeMode.FallbackFromLlmError => ReportingConstants.NarrativeModeFallbackFromLlmErrorTag,
        _ => ReportingConstants.NarrativeModeDeterministicTag
    };
}
