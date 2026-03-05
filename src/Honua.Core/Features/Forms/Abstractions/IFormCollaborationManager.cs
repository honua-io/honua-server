// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Forms.Domain;

namespace Honua.Core.Features.Forms.Abstractions;

/// <summary>
/// Manager for real-time form collaboration features.
/// </summary>
public interface IFormCollaborationManager
{
    /// <summary>
    /// Creates or joins a collaboration session.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="formId">Form identifier.</param>
    /// <param name="userId">User identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collaboration session info.</returns>
    Task<CollaborationSession> JoinSessionAsync(
        string sessionId,
        string formId,
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a user from a collaboration session.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="userId">User identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task LeaveSessionAsync(
        string sessionId,
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Broadcasts an update to all session participants.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="update">Form update to broadcast.</param>
    /// <param name="senderId">User who initiated the update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task BroadcastUpdateAsync(
        string sessionId,
        FormUpdate update,
        string senderId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets active participants in a session.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of active participant IDs.</returns>
    Task<List<string>> GetSessionParticipantsAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles conflict resolution when multiple users edit the same field.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="fieldId">Field identifier.</param>
    /// <param name="conflictingUpdates">List of conflicting updates.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resolved update.</returns>
    Task<FormUpdate> ResolveConflictAsync(
        string sessionId,
        string fieldId,
        List<FormUpdate> conflictingUpdates,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets collaboration session statistics.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Session statistics.</returns>
    Task<CollaborationSessionStats> GetSessionStatsAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}
