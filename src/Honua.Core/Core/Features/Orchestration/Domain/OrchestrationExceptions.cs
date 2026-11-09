// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Orchestration.Domain;

public sealed class WorkflowDefinitionValidationException : Exception
{
    public WorkflowDefinitionValidationException(string message)
        : base(message)
    {
        Failures = [message];
    }

    public WorkflowDefinitionValidationException(IReadOnlyList<string> failures)
        : base(failures.Count == 0 ? "Workflow definition is invalid." : string.Join("; ", failures))
    {
        Failures = failures;
    }

    public IReadOnlyList<string> Failures { get; }
}

public sealed class OrchestrationNotFoundException : Exception
{
    public OrchestrationNotFoundException(string message)
        : base(message)
    {
    }
}
