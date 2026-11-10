// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Domain;

// The server checks the proposal against the process catalog and never parses toolbox source or emulates arcpy.
public sealed record ToolboxTranslationManifest
{
    public string ArtifactKind { get; init; } = ToolboxTranslationArtifacts.ManifestKind;

    public string ArtifactVersion { get; init; } = "1.0";

    public required string ToolboxName { get; init; }

    public required string SourceFormat { get; init; }

    public string? SourceLabel { get; init; }

    public ToolboxToolDescriptor[] Tools { get; init; } = [];
}

public sealed record ToolboxToolDescriptor
{
    public required string ToolName { get; init; }

    public string? DisplayName { get; init; }

    public string? TargetProcessId { get; init; }

    public ToolboxParameterMapping[] ParameterMappings { get; init; } = [];

    // An untranslatable construct is reported as unsupported and is not dropped silently.
    public string[] UnsupportedConstructs { get; init; } = [];
}

public sealed record ToolboxParameterMapping
{
    public required string SourceName { get; init; }

    public required string TargetParameter { get; init; }

    public string? SourceDataType { get; init; }
}

public sealed record ToolboxTranslationReport
{
    public string ArtifactKind { get; init; } = ToolboxTranslationArtifacts.ReportKind;

    public string ArtifactVersion { get; init; } = "1.0";

    public required string ToolboxName { get; init; }

    public required string SourceFormat { get; init; }

    public required ToolboxTranslationSummary Summary { get; init; }

    public ToolboxToolTranslation[] Tools { get; init; } = [];
}

public sealed record ToolboxTranslationSummary
{
    public required int ToolCount { get; init; }

    public required int TranslatedCount { get; init; }

    public required int PartiallyTranslatedCount { get; init; }

    public required int UnsupportedCount { get; init; }
}

public sealed record ToolboxToolTranslation
{
    public required string ToolName { get; init; }

    public required string Classification { get; init; }

    public string? ProcessId { get; init; }

    public ToolboxParameterBinding[] ParameterBindings { get; init; } = [];

    public ToolboxTranslationIssue[] Issues { get; init; } = [];
}

public sealed record ToolboxParameterBinding
{
    public required string SourceName { get; init; }

    public required string TargetParameter { get; init; }

    public required string ValueType { get; init; }

    public required bool Required { get; init; }
}

public sealed record ToolboxTranslationIssue
{
    public required string Code { get; init; }

    public required string Message { get; init; }

    public string? ParameterName { get; init; }
}

// An incompatible artifact identity is rejected rather than read as the v1 contract.
public static class ToolboxTranslationArtifacts
{
    public const string ManifestKind = "honua.migration.toolbox-translation";

    public const string ReportKind = "honua.migration.toolbox-translation-report";

    public static readonly IReadOnlyList<string> SupportedManifestVersions = ["1.0"];
}

public static class ToolboxSourceFormats
{
    public const string Pyt = "pyt";

    public const string Tbx = "tbx";

    public const string Atbx = "atbx";

    public static readonly IReadOnlyList<string> All = [Pyt, Tbx, Atbx];
}

public static class ToolboxToolClassifications
{
    public const string Translated = "translated";

    public const string PartiallyTranslated = "partially-translated";

    // The tool has no executable native mapping and must not be presented as executable.
    public const string Unsupported = "unsupported";
}

public static class ToolboxTranslationIssueCodes
{
    public const string NoNativeExecutor = "no-native-executor";

    public const string UnknownProcess = "unknown-process";

    public const string UnknownTargetParameter = "unknown-target-parameter";

    public const string DuplicateTargetParameter = "duplicate-target-parameter";

    public const string MissingRequiredParameter = "missing-required-parameter";

    public const string UnsupportedConstruct = "unsupported-construct";

    // The mapping satisfies no member of a conditionally required input group, even though every statically required parameter is mapped.
    public const string UnsatisfiedConditionalInputs = "unsatisfied-conditional-inputs";

    public const string UnverifiableConditionalBranches = "unverifiable-conditional-branches";

    public const string ConditionalBranchRequirement = "conditional-branch-requirement";

    // The target process has no job entry point, so a translated tool can never run it through the job runtime.
    public const string ProcessNotJobExecutable = "process-not-job-executable";
}
