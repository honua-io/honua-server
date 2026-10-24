// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Admin.Domain;

/// <summary>
/// Allows one committed-sink publish to discover a schema outside the operational list.
/// The scope is entered only after the sink endpoint fingerprint matches the connection
/// string about to be used. Reserved schemas stay refused. Nested scopes restore the
/// previous depth when disposed.
/// </summary>
public static class CommittedExternalSchemaPublication
{
    private static readonly AsyncLocal<int> _depth = new();

    /// <summary>Whether the current async flow is inside <see cref="Begin"/>.</summary>
    public static bool IsActive => _depth.Value > 0;

    /// <summary>
    /// Enters a scope that may publish one non-reserved schema outside the operational list.
    /// </summary>
    /// <returns>A handle that restores the previous scope depth.</returns>
    public static IDisposable Begin()
    {
        var previous = _depth.Value;
        _depth.Value = previous + 1;
        return new Scope(previous);
    }

    /// <summary>
    /// Returns whether <paramref name="schema"/> may be discovered while this scope is active.
    /// </summary>
    /// <param name="schema">Candidate schema name.</param>
    /// <param name="isReserved">Whether the schema is metadata or a system catalog.</param>
    /// <param name="isValidIdentifier">Whether the schema is a safe identifier.</param>
    /// <returns><see langword="true"/> only inside an active scope for a valid, unreserved name.</returns>
    public static bool AllowsUnlisted(string? schema, bool isReserved, bool isValidIdentifier)
        => IsActive && isValidIdentifier && !isReserved && !string.IsNullOrWhiteSpace(schema);

    private sealed class Scope(int previous) : IDisposable
    {
        private int? _previous = previous;

        public void Dispose()
        {
            if (_previous is not int value)
            {
                return;
            }

            _previous = null;
            _depth.Value = value;
        }
    }
}
