// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.ServiceRegistration;
using Honua.Core.Features.WorkflowPackages.Generation;

namespace Honua.Ai.AiBuilder.Planning;

/// <summary>
/// Dedicated configuration seam for the server-side planner backing the
/// <c>honua_plan_analysis</c> MCP tool ("Honua-brings-LLM"). It lets an operator
/// gate and provider-select the live planner <em>independently</em> of the Studio
/// <c>WorkflowGeneration</c> surface, while reusing the same provider plumbing
/// (<c>WorkflowGeneration:Providers:*</c>).
/// </summary>
/// <remarks>
/// <para>
/// Precedence (see <see cref="AiBuilderServiceCollectionExtensions.ShouldUseLivePlanner"/>):
/// </para>
/// <list type="number">
///   <item>When <c>PlanAnalysis:Enabled</c> is set, it is authoritative — an
///   operator can force the live planner ON (or hold it OFF on the fixtures) for
///   the MCP plan lane without touching the Studio generation feature, and vice
///   versa.</item>
///   <item>When <c>PlanAnalysis:Enabled</c> is unset (null), the planner inherits
///   the <c>WorkflowGeneration</c> gate (back-compat: the original behaviour where
///   the live planner rode entirely on <c>WorkflowGeneration:Enabled</c>).</item>
///   <item><c>PlanAnalysis:Provider</c>, when set, overrides which provider id the
///   live planner asks the shared <c>WorkflowGeneration</c> seam to use; when unset
///   the planner falls back to <c>WorkflowGeneration:DefaultProvider</c>.</item>
/// </list>
/// <para>
/// This configuration intentionally carries no per-provider connection block of its
/// own. Endpoints, models, regions, and keys remain owned by
/// <see cref="WorkflowGenerationConfiguration.Providers"/> so there is a single
/// source of truth for provider credentials; <c>PlanAnalysis</c> only selects
/// among them.
/// </para>
/// </remarks>
public sealed class PlanAnalysisConfiguration
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "PlanAnalysis";

    /// <summary>
    /// Whether the live (provider-backed) planner is enabled for the MCP plan
    /// lane. <see langword="null"/> (the default / unset) means "inherit the
    /// <c>WorkflowGeneration</c> gate"; <see langword="true"/> forces the live
    /// planner on; <see langword="false"/> pins the deterministic fixture replay
    /// even when <c>WorkflowGeneration</c> is live.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// The provider id the live planner should use, overriding
    /// <c>WorkflowGeneration:DefaultProvider</c>. Must reference a provider id
    /// registered under <c>WorkflowGeneration:Providers</c> (for example
    /// <c>anthropic</c> or <c>bedrock</c>). <see langword="null"/> / empty falls
    /// back to the WorkflowGeneration default provider.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Returns the trimmed provider override, or <see langword="null"/> when no
    /// override is configured.
    /// </summary>
    public string? NormalizedProvider()
    {
        var provider = Provider?.Trim();
        return string.IsNullOrEmpty(provider) ? null : provider;
    }
}

/// <summary>
/// Validates <see cref="PlanAnalysisConfiguration"/> at startup. Only the provider
/// override is checked, against the same allow-list as
/// <see cref="WorkflowGenerationConfigurationValidator"/>; per-provider connection
/// fields are validated by WorkflowGeneration because PlanAnalysis selects, not
/// declares, providers.
/// </summary>
public sealed class PlanAnalysisConfigurationValidator : ConfigurationValidator<PlanAnalysisConfiguration>
{
    /// <inheritdoc />
    protected override void PerformFeatureSpecificValidation(PlanAnalysisConfiguration options, List<string> errors)
    {
        var provider = options.NormalizedProvider();
        if (provider is null)
        {
            // No override: WorkflowGeneration owns provider validation.
            return;
        }

        var isKnown =
            string.Equals(provider, WorkflowGenerationConfiguration.LocalProviderId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, WorkflowGenerationConfiguration.OpenAiProviderId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, WorkflowGenerationConfiguration.AnthropicProviderId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, WorkflowGenerationConfiguration.BedrockProviderId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, WorkflowGenerationConfiguration.DeterministicProviderId, StringComparison.OrdinalIgnoreCase);

        if (!isKnown)
        {
            errors.Add($"PlanAnalysis:Provider '{provider}' is not a supported provider id. "
                + "Supported values: 'local', 'openai', 'anthropic', 'bedrock', 'deterministic'.");
        }
    }
}
