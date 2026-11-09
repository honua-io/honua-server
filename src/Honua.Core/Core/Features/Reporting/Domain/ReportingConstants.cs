// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Reporting.Domain;

public static class ReportingConstants
{
    public const string ContractVersionV1 = "honua.report.v1";

    public const string GenericTemplateId = "analysis-report.generic";

    public const string UnsupportedContractVersionErrorCode = "report.contract.unsupported";

    public const string NarrativeModeTag = "report.narrative.mode";

    public const string NarrativeModeLlmAssistedTag = "llm-assisted";

    public const string NarrativeModeDeterministicTag = "deterministic";

    public const string NarrativeModeFallbackFromLlmErrorTag = "fallback-from-llm-error";
}
