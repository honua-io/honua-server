// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Abstractions;

public enum SpecCacheMode
{
    ReadWrite,

    ReadOnly,

    Bypass
}

public sealed record SpecApplyOptions
{
    public SpecCacheMode CacheMode { get; init; } = SpecCacheMode.ReadWrite;

    public int MaxConcurrency { get; init; } = 4;

    public TimeSpan? MutableSourceTtl { get; init; } = TimeSpan.FromMinutes(15);
}

public interface ISpecApplyEngine
{
    Task<SpecApplyHandle> StartAsync(
        CanonicalSpecDocument document,
        SpecApplyOptions options,
        CancellationToken cancellationToken);

    bool TryCancel(string applyToken);
}

public sealed class SpecApplyHandle
{
    public SpecApplyHandle(string applyToken, IAsyncEnumerable<SpecApplyEvent> events, SpecPlan plan)
    {
        ApplyToken = applyToken;
        Events = events;
        Plan = plan;
    }

    public string ApplyToken { get; }

    public SpecPlan Plan { get; }

    public IAsyncEnumerable<SpecApplyEvent> Events { get; }
}
