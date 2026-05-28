// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Events.Outbox;

namespace Honua.Server.Features.Infrastructure.Events.Outbox;

/// <summary>
/// Default capability provider used when no backing-store extension has registered one
/// (e.g. test hosts that bypass <c>RegisterInfrastructureServices</c>). Reports the
/// outbox as unsupported so the dispatcher exits cleanly and protocol handlers fall
/// back to the post-commit publish + retry queue path.
/// </summary>
internal sealed class NoOpOutboxCapabilityProvider : IOutboxCapabilityProvider
{
    public bool SupportsTransactionalOutbox => false;

    public string? CapabilityLimitationDescription =>
        "No backing-store outbox capability provider is registered; feature mutations fall back to the post-commit publish + retry queue path.";
}
