// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Reporting.Abstractions;
using Honua.Core.Features.Reporting.Domain;
using Honua.Core.Features.Reporting.Templates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Reporting.Services;

/// <summary>
/// Composes the canonical <see cref="AnalysisReport"/> envelope. Always invokes
/// the deterministic narrative provider first so every slot has factual text;
/// the optional LLM provider may then enrich slots, but a failure or timeout
/// falls back to the deterministic baseline and stamps
/// <see cref="NarrativeMode.FallbackFromLlmError"/>.
/// </summary>
internal sealed class AnalysisReportBuilder : IAnalysisReportBuilder
{
    private readonly IAnalysisReportTemplateRegistry _templates;
    private readonly DeterministicNarrativeProvider _deterministic;
    private readonly INarrativeProvider? _llmProvider;
    private readonly ReportingConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AnalysisReportBuilder> _logger;

    /// <summary>
    /// Builds an instance. <paramref name="llmProvider"/> is optional; when
    /// not registered the builder runs entirely on the deterministic path.
    /// </summary>
    public AnalysisReportBuilder(
        IAnalysisReportTemplateRegistry templates,
        DeterministicNarrativeProvider deterministic,
        IOptions<ReportingConfiguration> configuration,
        TimeProvider timeProvider,
        ILogger<AnalysisReportBuilder> logger,
        INarrativeProvider? llmProvider = null)
    {
        _templates = templates;
        _deterministic = deterministic;
        _llmProvider = llmProvider is { IsDeterministic: false } ? llmProvider : null;
        _configuration = configuration.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AnalysisReport> BuildAsync(
        string jobId,
        AnalysisResultPackage package,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentNullException.ThrowIfNull(package);

        var processId = ResolveProcessId(package);
        var template = _templates.ResolveOrFallback(processId);
        var draft = template.Build(package);
        cancellationToken.ThrowIfCancellationRequested();

        var deterministicFill = await _deterministic
            .GenerateAsync(draft, cancellationToken)
            .ConfigureAwait(false);
        var narrativeMode = NarrativeMode.Deterministic;
        var slotFill = deterministicFill.SlotText;

        if (_configuration.Narrative.Enabled && _llmProvider is not null)
        {
            try
            {
                var llmFill = await _llmProvider
                    .GenerateAsync(draft, cancellationToken)
                    .ConfigureAwait(false);

                if (llmFill.SlotText.Count > 0)
                {
                    slotFill = MergeFill(deterministicFill.SlotText, llmFill.SlotText);
                    narrativeMode = NarrativeMode.LlmAssisted;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AnalysisReportLog.NarrativeFallback(_logger, draft.TemplateId, ex.GetType().Name, ex.Message);
                narrativeMode = NarrativeMode.FallbackFromLlmError;
            }
        }

        var generatedAt = _timeProvider.GetUtcNow();
        var sections = ComposeSections(draft, slotFill, narrativeMode, jobId, package, generatedAt);

        return new AnalysisReport
        {
            ReportId = BuildReportId(jobId, package),
            ReportContractVersion = ReportingConstants.ContractVersionV1,
            JobId = jobId,
            ResultPackageId = package.ResultPackageId,
            ProcessId = draft.ProcessId,
            ProcessFamily = draft.ProcessFamily,
            TemplateId = draft.TemplateId,
            TemplateVersion = draft.TemplateVersion,
            Summary = package.Summary,
            Sections = sections,
            NarrativeMode = narrativeMode,
            Provenance = package.Provenance,
            GeneratedAt = generatedAt,
            Assumptions = package.Assumptions
        };
    }

    private static Dictionary<string, string> MergeFill(
        IReadOnlyDictionary<string, string> deterministic,
        IReadOnlyDictionary<string, string> llm)
    {
        var merged = new Dictionary<string, string>(deterministic.Count, StringComparer.Ordinal);
        foreach (var kvp in deterministic)
        {
            merged[kvp.Key] = llm.TryGetValue(kvp.Key, out var llmText) && !string.IsNullOrWhiteSpace(llmText)
                ? llmText
                : kvp.Value;
        }
        return merged;
    }

    private static List<AnalysisReportSection> ComposeSections(
        AnalysisReportDraft draft,
        IReadOnlyDictionary<string, string> slotFill,
        NarrativeMode mode,
        string jobId,
        AnalysisResultPackage package,
        DateTimeOffset generatedAt)
    {
        var sections = new List<AnalysisReportSection>(draft.Sections.Count + draft.NarrativeSlots.Count + 1);
        sections.AddRange(draft.Sections);

        foreach (var slot in draft.NarrativeSlots)
        {
            var deterministicText = slot.DeterministicText;
            var llmText = slotFill.TryGetValue(slot.SlotId, out var filled) && !string.Equals(filled, deterministicText, StringComparison.Ordinal)
                ? filled
                : null;
            var slotMode = mode switch
            {
                NarrativeMode.LlmAssisted when llmText is not null => NarrativeMode.LlmAssisted,
                NarrativeMode.LlmAssisted => NarrativeMode.Deterministic,
                _ => mode
            };

            if (!string.IsNullOrEmpty(slot.Heading))
            {
                sections.Add(new HeadingSection { Text = slot.Heading, Level = 2 });
            }

            sections.Add(new NarrativeSection
            {
                SlotId = slot.SlotId,
                DeterministicText = deterministicText,
                LlmText = llmText,
                Mode = slotMode
            });
        }

        sections.Add(TemplateBuildHelpers.BuildProvenanceFooter(jobId, package, generatedAt));
        return sections;
    }

    private static string BuildReportId(string jobId, AnalysisResultPackage package)
    {
        var input = string.Create(
            CultureInfo.InvariantCulture,
            $"{jobId}|{ReportingConstants.ContractVersionV1}|{package.ResultPackageId}");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var fingerprint = Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        return $"report-{jobId}-{fingerprint}";
    }

    private static string ResolveProcessId(AnalysisResultPackage package)
    {
        if (package.Provenance.ProcessDefinitions.Count > 0)
        {
            return package.Provenance.ProcessDefinitions[0];
        }
        return string.Empty;
    }
}
