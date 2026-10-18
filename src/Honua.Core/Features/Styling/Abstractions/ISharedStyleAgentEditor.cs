// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Styling.Abstractions;

/// <summary>Frozen shared-style reads and canonical human-reviewed replacement proposals.</summary>
public interface ISharedStyleAgentEditor
{
    /// <summary>Reads only the exact authorized shared-style task captured by this turn.</summary>
    Task<SharedStyleAgentRead> ReadAsync(ClaimsPrincipal principal, string workspaceId, string turnId,
        CancellationToken cancellationToken = default);

    /// <summary>Proposes retained before-bytes from a verified successful canonical receipt against the current frozen task.</summary>
    Task<OperationHandle> ProposeRestoreAsync(ClaimsPrincipal principal, string workspaceId, string turnId,
        string sourceProposalId, CancellationToken cancellationToken = default);

    /// <summary>Proposes exact full canonical style replacement; never approves or directly executes it.</summary>
    Task<OperationHandle> ProposeAsync(ClaimsPrincipal principal, string workspaceId, string turnId,
        SharedStyleEditIntent intent, CancellationToken cancellationToken = default);
}

/// <summary>Model input contains immutable target pins and proposed style bytes, never authority or credentials.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SharedStyleEditIntent(string StyleId, int StyleVersion, string ContentHash,
    string ImpactHash, JsonElement Style);

/// <summary>Actual canonical bytes and authorized impact, without a rendering or execution qualification claim.</summary>
public sealed record SharedStyleAgentRead(StyleEditImpact Impact, JsonElement Style,
    string Qualification = "canonical-style-read-not-render-qualified");
