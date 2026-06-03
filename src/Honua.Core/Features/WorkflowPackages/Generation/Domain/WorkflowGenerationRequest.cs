// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Core.Features.WorkflowPackages.Generation.Domain;

/// <summary>
/// Service-level request for a natural-language workflow generation turn. The host service
/// grounds this in the node registry, selects a provider, runs the provider, and applies the
/// hard validation gate before returning a <see cref="WorkflowGenerationResult"/>.
/// </summary>
public sealed record WorkflowGenerationRequest
{
    /// <summary>The natural-language prompt for this turn.</summary>
    public required string Prompt { get; init; }

    /// <summary>Provider id to use; null selects the configured default provider.</summary>
    public string? ProviderId { get; init; }

    /// <summary>Optional per-call model override.</summary>
    public string? ModelOverride { get; init; }

    /// <summary>The current graph when refining an existing draft; null for fresh generation.</summary>
    public WorkflowGraph? CurrentGraph { get; init; }

    /// <summary>Prior conversation turns for context.</summary>
    public IReadOnlyList<WorkflowGenerationConversationTurn> Conversation { get; init; } = [];

    /// <summary>Answers to a prior clarification turn.</summary>
    public IReadOnlyList<WorkflowGenerationAnswer> Answers { get; init; } = [];
}

/// <summary>
/// HTTP request body for <c>POST /api/v1/console/workflow-packages/generate</c>. The console shim
/// (Honua.Console.Contracts) posts this shape; the endpoint maps it to a
/// <see cref="WorkflowGenerationRequest"/>.
/// </summary>
public sealed record GenerateWorkflowPackageRequest
{
    /// <summary>The natural-language prompt.</summary>
    public string Prompt { get; init; } = string.Empty;

    /// <summary>Provider id to use; null selects the server default.</summary>
    public string? Provider { get; init; }

    /// <summary>Optional per-call model override.</summary>
    public string? Model { get; init; }

    /// <summary>Current graph for a refine turn; null requests fresh generation.</summary>
    public WorkflowGraph? Graph { get; init; }

    /// <summary>Prior conversation turns for context.</summary>
    public IReadOnlyList<WorkflowGenerationConversationTurn> Conversation { get; init; } = [];

    /// <summary>Answers to a prior needs-clarification turn.</summary>
    public IReadOnlyList<WorkflowGenerationAnswer> Answers { get; init; } = [];
}
