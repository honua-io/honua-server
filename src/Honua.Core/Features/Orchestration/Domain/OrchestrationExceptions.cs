// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Orchestration.Domain;

/// <summary>
/// Thrown when a workflow definition fails structural validation.
/// </summary>
public sealed class WorkflowDefinitionValidationException : Exception
{
    /// <summary>
    /// Initializes a new instance with a single-line failure message.
    /// </summary>
    public WorkflowDefinitionValidationException(string message)
        : base(message)
    {
        Failures = [message];
    }

    /// <summary>
    /// Initializes a new instance with structured failure messages.
    /// </summary>
    public WorkflowDefinitionValidationException(IReadOnlyList<string> failures)
        : base(failures.Count == 0 ? "Workflow definition is invalid." : string.Join("; ", failures))
    {
        Failures = failures;
    }

    /// <summary>
    /// Structural validation failures produced by <see cref="WorkflowDefinitionValidator"/>.
    /// </summary>
    public IReadOnlyList<string> Failures { get; }
}

/// <summary>
/// Thrown when a requested workflow definition or run cannot be located.
/// </summary>
public sealed class OrchestrationNotFoundException : Exception
{
    /// <summary>
    /// Initializes a new exception.
    /// </summary>
    public OrchestrationNotFoundException(string message)
        : base(message)
    {
    }
}
