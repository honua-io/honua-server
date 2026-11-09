// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

public sealed record UploadResult
{
    public required bool Success { get; init; }

    public CloudFile? File { get; init; }

    public string? ErrorMessage { get; init; }

    public TimeSpan Duration { get; init; }

    public static UploadResult CreateSuccess(CloudFile file, TimeSpan duration = default)
        => new()
        {
            Success = true,
            File = file,
            Duration = duration
        };

    public static UploadResult CreateFailure(string errorMessage, TimeSpan duration = default)
        => new()
        {
            Success = false,
            ErrorMessage = errorMessage,
            Duration = duration
        };
}
