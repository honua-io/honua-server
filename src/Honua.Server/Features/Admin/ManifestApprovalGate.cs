// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Server.Features.Admin.Models;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Scoped façade that groups the manifest-approval dependencies into a single injection point,
/// keeping endpoint parameter counts within the project's 5-dependency limit.
/// </summary>
internal sealed class ManifestApprovalGate(
    IManifestPendingChangeStore pendingStore,
    IOptions<ManifestApprovalOptions> options,
    ManifestApprovalWebhookDispatcher? webhookDispatcher = null)
{
    /// <summary>
    /// The pending change store for approval records.
    /// </summary>
    public IManifestPendingChangeStore PendingStore { get; } = pendingStore;

    /// <summary>
    /// Whether the approval workflow feature is enabled.
    /// </summary>
    public bool Enabled => options.Value.Enabled;

    /// <summary>
    /// The approval workflow configuration.
    /// </summary>
    public ManifestApprovalOptions Options => options.Value;

    /// <summary>
    /// Enqueues an approval webhook event for delivery, if a dispatcher is configured.
    /// </summary>
    public void EnqueueWebhook(ManifestApprovalWebhookEvent webhookEvent)
    {
        webhookDispatcher?.Enqueue(webhookEvent);
    }
}
