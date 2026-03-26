// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Threading;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Server.Features.Infrastructure.Middleware;

/// <summary>
/// Mutable schema context for request-scoped database operations.
/// </summary>
internal sealed class SchemaContext : ISchemaContext
{
    private static readonly AsyncLocal<string?> AmbientSchema = new();

    /// <inheritdoc />
    public string? CurrentSchema
    {
        get => AmbientSchema.Value;
        set => AmbientSchema.Value = value;
    }

    internal static string? AmbientCurrentSchema => AmbientSchema.Value;
}
