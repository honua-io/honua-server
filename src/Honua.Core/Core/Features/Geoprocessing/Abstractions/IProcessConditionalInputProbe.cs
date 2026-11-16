// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Abstractions;

public interface IProcessConditionalInputProbe
{
    IReadOnlyList<ProcessAdmissibilityViolation> FindAdmissibilityViolations(
        string processId,
        IReadOnlyCollection<string> suppliedParameterNames);

    IReadOnlyList<string> FindUnverifiableConditionalParameters(
        string processId,
        IReadOnlyCollection<string> suppliedParameterNames);

    IReadOnlyList<ProcessBranchRequirement> FindConditionalBranchRequirements(
        string processId,
        IReadOnlyCollection<string> suppliedParameterNames);
}

public readonly record struct ProcessBranchRequirement(
    string Branch,
    string ParameterName,
    string Message);

public readonly record struct ProcessAdmissibilityViolation(
    ProcessAdmissibilityViolationKind Kind,
    string Message);

public enum ProcessAdmissibilityViolationKind
{
    Inputs,

    NotJobExecutable
}
