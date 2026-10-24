// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Threading;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Infrastructure.Middleware;

/// <summary>
/// Mutable schema context for request-scoped database operations.
/// </summary>
internal sealed class SchemaContext : ISchemaContext
{
    private static readonly AsyncLocal<string?> AmbientSchema = new();
    private string? _currentSchema;

    /// <inheritdoc />
    public string? CurrentSchema
    {
        get => AmbientSchema.Value ?? _currentSchema;
        set
        {
            _currentSchema = value;
            AmbientSchema.Value = value;
        }
    }

    internal static string? AmbientCurrentSchema => AmbientSchema.Value;
}
