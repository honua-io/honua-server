// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Operations.Abstractions;

/// <summary>
/// Names operations whose MCP projection has been audited and committed to the Admin
/// projection manifest, so they publish as MCP tools by default (honua-server#3363).
/// Operations outside every registered projection publish only when the full operations
/// catalog is explicitly opted in.
/// </summary>
public interface IAuditedAdminMcpProjection
{
    /// <summary>
    /// The audited operation ids. Audited exclusions are withheld from MCP regardless of
    /// whether they appear here.
    /// </summary>
    IReadOnlyCollection<string> OperationIds { get; }
}
