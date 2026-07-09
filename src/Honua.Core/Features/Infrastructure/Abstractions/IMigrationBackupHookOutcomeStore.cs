// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Stores the latest observed pre-migration backup hook outcome for admin observability.
/// </summary>
public interface IMigrationBackupHookOutcomeStore
{
    /// <summary>
    /// Gets the latest recorded backup hook outcome, or <see langword="null"/> when no hook has run.
    /// </summary>
    MigrationBackupHookOutcome? LastOutcome { get; }

    /// <summary>
    /// Records a backup hook outcome.
    /// </summary>
    /// <param name="outcome">Outcome to publish.</param>
    void Record(MigrationBackupHookOutcome outcome);
}
