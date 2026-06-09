// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Forms.Packages;

namespace Honua.Ai.FormGeneration;

/// <summary>
/// Grounds a natural-language prompt in the form field-type vocabulary, runs the configured
/// generation provider, and applies <see cref="FormPackageValidator"/> as a generation-lenient
/// gate (structural failures only; target binding is deferred to publish) with a bounded repair
/// loop — so the client never receives a structurally-invalid form. The form-family counterpart to
/// the workflow generation service.
/// </summary>
public interface IFormGenerationService
{
    Task<FormGenerationResult> GenerateAsync(FormGenerationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reports whether NL form generation is enabled + the configured providers (drives the console's
    /// from-prompt availability). Mirrors the workflow-generation providers contract.</summary>
    Task<FormGenerationProviders> GetProvidersAsync(CancellationToken cancellationToken = default);
}

/// <summary>Service-level request to generate or refine a form from a prompt.</summary>
public sealed record FormGenerationRequest
{
    public required string Prompt { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public FormPackageDocument? CurrentForm { get; init; }
    public IReadOnlyList<FormGenerationConversationTurn> Conversation { get; init; } = [];
    public IReadOnlyList<FormGenerationAnswer> Answers { get; init; } = [];
}

/// <summary>
/// Generation result. JSON property names match the console wire contract
/// (<c>HonuaFormGenerationResult</c>): status, package, rationale, clarifications, validation,
/// unmappedRequests, capabilityState, provider, model.
/// </summary>
public sealed record FormGenerationResult
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("package")]
    public FormPackageDocument? Package { get; init; }

    [JsonPropertyName("rationale")]
    public string? Rationale { get; init; }

    [JsonPropertyName("clarifications")]
    public IReadOnlyList<FormGenerationClarification> Clarifications { get; init; } = [];

    [JsonPropertyName("validation")]
    public FormPackageValidationResult? Validation { get; init; }

    [JsonPropertyName("unmappedRequests")]
    public IReadOnlyList<string> UnmappedRequests { get; init; } = [];

    [JsonPropertyName("capabilityState")]
    public FormGenerationCapabilityState? CapabilityState { get; init; }

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }
}

/// <summary>HTTP request DTO for <c>POST /api/v1/admin/forms/packages/generate</c> (mirrors the console <c>GenerateFormPackageRequest</c>).</summary>
public sealed record GenerateFormPackageRequest
{
    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = string.Empty;

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("package")]
    public FormPackageDocument? Package { get; init; }

    [JsonPropertyName("conversation")]
    public FormGenerationConversationTurn[] Conversation { get; init => field = value ?? []; } = [];

    [JsonPropertyName("answers")]
    public FormGenerationAnswer[] Answers { get; init => field = value ?? []; } = [];
}

public sealed record FormGenerationConversationTurn
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;
}

public sealed record FormGenerationAnswer
{
    [JsonPropertyName("questionId")]
    public string QuestionId { get; init; } = string.Empty;

    [JsonPropertyName("optionId")]
    public string OptionId { get; init; } = string.Empty;
}

public sealed record FormGenerationClarification
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("choices")]
    public IReadOnlyList<FormGenerationClarificationChoice> Choices { get; init; } = [];
}

public sealed record FormGenerationClarificationChoice
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("effect")]
    public string? Effect { get; init; }
}

public sealed record FormGenerationCapabilityState
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>Result of <c>GET /api/v1/admin/forms/packages/generation/providers</c>: whether NL form
/// generation is enabled on this server, the default provider, and the configured providers — so the console
/// can show the from-prompt builder as available (mirrors the workflow-generation providers contract).</summary>
public sealed record FormGenerationProviders
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("defaultProvider")]
    public string? DefaultProvider { get; init; }

    [JsonPropertyName("providers")]
    public IReadOnlyList<FormGenerationProviderInfo> Providers { get; init; } = [];
}

public sealed record FormGenerationProviderInfo
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("available")]
    public bool Available { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}

/// <summary>Source-generated JSON context for the form-generation HTTP request/response DTOs.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GenerateFormPackageRequest))]
[JsonSerializable(typeof(FormGenerationResult))]
[JsonSerializable(typeof(FormGenerationProviders))]
public sealed partial class FormGenerationApiJsonContext : JsonSerializerContext;
