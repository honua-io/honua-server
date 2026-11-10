// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

public enum ProgressCompareAndSetOutcome
{
    Updated,

    NotFound,

    StatusMismatch
}

public sealed record ProgressCompareAndSetResult
{
    public required ProgressCompareAndSetOutcome Outcome { get; init; }

    public IOperationProgress? CurrentProgress { get; init; }

    public static ProgressCompareAndSetResult Updated { get; } = new() { Outcome = ProgressCompareAndSetOutcome.Updated };

    public static ProgressCompareAndSetResult NotFound { get; } = new() { Outcome = ProgressCompareAndSetOutcome.NotFound };

    public static ProgressCompareAndSetResult StatusMismatch(IOperationProgress currentProgress)
        => new() { Outcome = ProgressCompareAndSetOutcome.StatusMismatch, CurrentProgress = currentProgress };
}
