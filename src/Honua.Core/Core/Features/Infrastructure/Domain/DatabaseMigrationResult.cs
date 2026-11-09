// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

public sealed record DatabaseMigrationResult
{
    public required bool Successful { get; init; }

    public IReadOnlyList<string> AppliedScripts { get; init; } = Array.Empty<string>();

    public string? ErrorMessage { get; init; }

    public Exception? Error { get; init; }

    public static DatabaseMigrationResult Succeeded(IReadOnlyList<string>? appliedScripts = null)
        => new()
        {
            Successful = true,
            AppliedScripts = appliedScripts ?? Array.Empty<string>()
        };

    public static DatabaseMigrationResult Failed(
        Exception error,
        string? errorMessage = null,
        IReadOnlyList<string>? appliedScripts = null)
        => new()
        {
            Successful = false,
            Error = error,
            ErrorMessage = errorMessage ?? error.Message,
            AppliedScripts = appliedScripts ?? Array.Empty<string>()
        };
}
