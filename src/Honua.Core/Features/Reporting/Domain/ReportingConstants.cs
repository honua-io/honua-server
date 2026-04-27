// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Reporting.Domain;

/// <summary>
/// Constants for the analysis reporting contract. Versioning lives here so
/// renderers, persistence, and clients can pin against a single source.
/// </summary>
public static class ReportingConstants
{
    /// <summary>
    /// The current report contract version. Renderers refuse contract versions
    /// they do not recognize via <see cref="UnsupportedContractVersionErrorCode"/>.
    /// </summary>
    public const string ContractVersionV1 = "honua.report.v1";

    /// <summary>
    /// Stable identifier for the generic fallback template used when no
    /// process-specific template is registered.
    /// </summary>
    public const string GenericTemplateId = "analysis-report.generic";

    /// <summary>
    /// Error code emitted when a renderer encounters a report contract version
    /// it does not support.
    /// </summary>
    public const string UnsupportedContractVersionErrorCode = "report.contract.unsupported";

    /// <summary>
    /// Telemetry tag name reporting which narrative path produced the report.
    /// </summary>
    public const string NarrativeModeTag = "report.narrative.mode";

    /// <summary>
    /// Telemetry tag value indicating the narrative was produced by the LLM
    /// provider on top of deterministic scaffolding.
    /// </summary>
    public const string NarrativeModeLlmAssistedTag = "llm-assisted";

    /// <summary>
    /// Telemetry tag value indicating the narrative was produced entirely by
    /// the deterministic provider.
    /// </summary>
    public const string NarrativeModeDeterministicTag = "deterministic";

    /// <summary>
    /// Telemetry tag value indicating the narrative fell back to deterministic
    /// text after an LLM failure or timeout.
    /// </summary>
    public const string NarrativeModeFallbackFromLlmErrorTag = "fallback-from-llm-error";
}
