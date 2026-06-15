// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Guardrails.Domain;

namespace Honua.Core.Features.Guardrails;

/// <summary>
/// Operator-configurable overrides for the edition guardrail ladder. The default
/// policy (Community/Pro → DirectExecute; Enterprise → RequiresApproval for
/// in-scope mutating classes) applies when no override is configured.
/// </summary>
public sealed class GuardrailLadderOptions
{
    /// <summary>
    /// Configuration section that binds to these options.
    /// </summary>
    public const string SectionName = "Guardrails";

    /// <summary>
    /// When <see langword="true"/>, an unknown or unmapped operation class fails
    /// closed to <see cref="GuardrailTier.RequiresApproval"/> in production. When
    /// <see langword="false"/> (development/test default behavior), unmapped
    /// classes resolve to <see cref="GuardrailTier.DirectExecute"/>. The server
    /// host forces this to <see langword="true"/> outside of development.
    /// </summary>
    public bool FailClosed { get; set; }

    /// <summary>
    /// Per-operation-class tier overrides keyed by operation class name
    /// (for example <c>"Deploy"</c>) mapping to a tier name (for example
    /// <c>"RequiresApproval"</c>). Overrides let operators tighten or loosen the
    /// default ladder. Invalid keys/values are ignored.
    /// </summary>
    public IDictionary<string, string> Overrides { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
