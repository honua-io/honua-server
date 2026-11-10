// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Admin.Domain;

public static class CommittedExternalSchemaPublication
{
    private static readonly AsyncLocal<int> _depth = new();

    public static bool IsActive => _depth.Value > 0;

    public static IDisposable Begin()
    {
        var previous = _depth.Value;
        _depth.Value = previous + 1;
        return new Scope(previous);
    }

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
