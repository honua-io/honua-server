// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Proto = Honua.Server.Features.Grpc.Proto.V2;

namespace Honua.Server.Features.Infrastructure.Services;

/// <summary>
/// Implementation of sync session for managing bidirectional synchronization state.
/// Tracks changes, conflicts, and session statistics during sync operations.
/// </summary>
internal sealed class SyncSession : ISyncSession
{
    private readonly IFeatureReader _featureReader;
    private readonly IFeatureWriter _featureWriter;
    private readonly IChangeTracker _changeTracker;
    private readonly ILogger<SyncSession> _logger;

    // Session state
    private SyncSessionState _state = SyncSessionState.Created;
    private Proto.SyncMetadata? _metadata;
    private readonly SyncSessionStatistics _statistics = new();
    private readonly List<Proto.ConflictResolution> _resolvedConflicts = new();

    // Session tracking
    public string SessionId { get; }
    public SyncSessionState State => _state;
    public int ChangesApplied { get; private set; }
    public int ConflictsResolved { get; private set; }
    public DateTime StartTime { get; } = DateTime.UtcNow;

    // Events
    internal event EventHandler? SessionDisposed;

    public SyncSession(
        string sessionId,
        IFeatureReader featureReader,
        IFeatureWriter featureWriter,
        IChangeTracker changeTracker,
        ILogger<SyncSession> logger)
    {
        SessionId = sessionId;
        _featureReader = featureReader;
        _featureWriter = featureWriter;
        _changeTracker = changeTracker;
        _logger = logger;
    }

    public async Task InitializeAsync(Proto.SyncMetadata metadata, CancellationToken cancellationToken = default)
    {
        using var activity = System.Diagnostics.Activity.Current?.Source.StartActivity("sync_session_initialize");
        activity?.SetTag("session_id", SessionId);
        activity?.SetTag("client_id", metadata.ClientId);

        if (_state != SyncSessionState.Created)
        {
            throw new InvalidOperationException($"Session {SessionId} is already initialized (state: {_state})");
        }

        _metadata = metadata;
        _state = SyncSessionState.Initialized;

        _logger.LogDebug("Initialized sync session {SessionId} for client {ClientId}",
            SessionId, metadata.ClientId);
    }

    public async Task<Proto.ChangeProcessingResult> ProcessChangesAsync(
        IEnumerable<Proto.FeatureChange> changes,
        CancellationToken cancellationToken = default)
    {
        using var activity = System.Diagnostics.Activity.Current?.Source.StartActivity("sync_session_process_changes");
        activity?.SetTag("session_id", SessionId);

        if (_state != SyncSessionState.Initialized && _state != SyncSessionState.ProcessingChanges)
        {
            throw new InvalidOperationException($"Cannot process changes in state {_state}");
        }

        _state = SyncSessionState.ProcessingChanges;

        var changesList = changes.ToList();
        var results = new List<Proto.ChangeResult>();
        var conflicts = new List<Proto.FeatureConflict>();

        activity?.SetTag("change_count", changesList.Count);

        foreach (var change in changesList)
        {
            try
            {
                var result = await ProcessSingleChangeAsync(change, cancellationToken);
                results.Add(result);

                if (result.Success)
                {
                    ChangesApplied++;
                    UpdateStatistics(change.Operation);
                }
                else if (result.Conflict != null)
                {
                    conflicts.Add(result.Conflict);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing change {ChangeId} in session {SessionId}",
                    change.ChangeId, SessionId);

                results.Add(new Proto.ChangeResult
                {
                    ChangeId = change.ChangeId,
                    Success = false,
                    Error = new Proto.Error
                    {
                        Code = Proto.ErrorCode.ServiceUnavailable,
                        Message = ex.Message
                    }
                });
            }
        }

        if (conflicts.Any())
        {
            _state = SyncSessionState.ResolvingConflicts;
        }

        return new Proto.ChangeProcessingResult
        {
            Results = { results },
            Conflicts = { conflicts },
            SuccessCount = results.Count(r => r.Success),
            FailureCount = results.Count(r => !r.Success && r.Conflict == null),
            ConflictCount = conflicts.Count
        };
    }

    public async Task RecordConflictResolutionsAsync(
        IEnumerable<Proto.ConflictResolution> resolutions,
        CancellationToken cancellationToken = default)
    {
        if (_state != SyncSessionState.ResolvingConflicts)
        {
            throw new InvalidOperationException($"Cannot record conflict resolutions in state {_state}");
        }

        _resolvedConflicts.AddRange(resolutions);
        ConflictsResolved += resolutions.Count();

        _logger.LogDebug("Recorded {ResolutionCount} conflict resolutions in session {SessionId}",
            resolutions.Count(), SessionId);

        // Return to processing changes if all conflicts are resolved
        if (!HasPendingConflicts())
        {
            _state = SyncSessionState.ProcessingChanges;
        }
    }

    public async Task<long> GetFinalGenerationAsync(CancellationToken cancellationToken = default)
    {
        _state = SyncSessionState.Completing;

        var finalGeneration = await _changeTracker.GetCurrentGenerationAsync(cancellationToken);

        _state = SyncSessionState.Completed;

        _logger.LogDebug("Completed sync session {SessionId} at generation {Generation}",
            SessionId, finalGeneration);

        return finalGeneration;
    }

    public SyncSessionStatistics GetStatistics()
    {
        var duration = DateTime.UtcNow - StartTime;

        return new SyncSessionStatistics
        {
            FeaturesAdded = _statistics.FeaturesAdded,
            FeaturesUpdated = _statistics.FeaturesUpdated,
            FeaturesDeleted = _statistics.FeaturesDeleted,
            ConflictsDetected = _statistics.ConflictsDetected,
            ConflictsResolved = ConflictsResolved,
            ConflictsPending = _statistics.ConflictsDetected - ConflictsResolved,
            Duration = duration,
            BytesTransferred = _statistics.BytesTransferred,
            CompressionRatio = _statistics.CompressionRatio
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_state != SyncSessionState.Completed && _state != SyncSessionState.Failed)
        {
            _state = SyncSessionState.Cancelled;
            _logger.LogWarning("Sync session {SessionId} was disposed before completion (state: {State})",
                SessionId, _state);
        }

        SessionDisposed?.Invoke(this, EventArgs.Empty);

        _logger.LogDebug("Disposed sync session {SessionId} after {Duration}",
            SessionId, DateTime.UtcNow - StartTime);
    }

    #region Private Methods

    private async Task<Proto.ChangeResult> ProcessSingleChangeAsync(
        Proto.FeatureChange change,
        CancellationToken cancellationToken)
    {
        // Check for conflicts first
        var serverChanges = await _changeTracker.GetChangesSinceAsync(
            change.LayerId,
            change.BaseGeneration,
            cancellationToken);

        var conflictingChange = serverChanges.FirstOrDefault(sc =>
            sc.ObjectId == change.ObjectId &&
            sc.LayerId == change.LayerId);

        if (conflictingChange != null)
        {
            var conflict = await CreateConflictAsync(change, conflictingChange, cancellationToken);
            return new Proto.ChangeResult
            {
                ChangeId = change.ChangeId,
                Success = false,
                Conflict = conflict
            };
        }

        // Apply the change
        try
        {
            await ApplyChangeAsync(change, cancellationToken);

            return new Proto.ChangeResult
            {
                ChangeId = change.ChangeId,
                Success = true,
                NewGeneration = await _changeTracker.GetCurrentGenerationAsync(cancellationToken)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply change {ChangeId}", change.ChangeId);

            return new Proto.ChangeResult
            {
                ChangeId = change.ChangeId,
                Success = false,
                Error = new Proto.Error
                {
                    Code = Proto.ErrorCode.ServiceUnavailable,
                    Message = ex.Message
                }
            };
        }
    }

    private async Task ApplyChangeAsync(Proto.FeatureChange change, CancellationToken cancellationToken)
    {
        var editBatch = change.Operation switch
        {
            Proto.FeatureOperation.Add => CreateAddEditBatch(change),
            Proto.FeatureOperation.Update => CreateUpdateEditBatch(change),
            Proto.FeatureOperation.Delete => CreateDeleteEditBatch(change),
            _ => throw new ArgumentException($"Unsupported operation: {change.Operation}")
        };

        await _featureWriter.ApplyEditsAsync(change.LayerId, editBatch, cancellationToken);
    }

    private FeatureEditBatch CreateAddEditBatch(Proto.FeatureChange change)
    {
        if (change.Feature == null)
            throw new ArgumentException("Feature data required for add operation");

        var feature = ConvertFromProtoFeature(change.Feature);
        return new FeatureEditBatch { Adds = new[] { feature } };
    }

    private FeatureEditBatch CreateUpdateEditBatch(Proto.FeatureChange change)
    {
        if (change.Feature == null)
            throw new ArgumentException("Feature data required for update operation");

        var feature = ConvertFromProtoFeature(change.Feature);
        return new FeatureEditBatch { Updates = new[] { feature } };
    }

    private FeatureEditBatch CreateDeleteEditBatch(Proto.FeatureChange change)
    {
        return new FeatureEditBatch { Deletes = new[] { change.ObjectId } };
    }

    private async Task<Proto.FeatureConflict> CreateConflictAsync(
        Proto.FeatureChange clientChange,
        FeatureChange serverChange,
        CancellationToken cancellationToken)
    {
        var conflict = new Proto.FeatureConflict
        {
            ConflictId = Guid.NewGuid().ToString("N"),
            LayerId = clientChange.LayerId,
            ObjectId = clientChange.ObjectId,
            ClientOperation = clientChange.Operation,
            ServerOperation = ConvertOperation(serverChange.Operation),
            ClientGeneration = clientChange.BaseGeneration,
            ServerGeneration = serverChange.Generation,
            ConflictType = DetermineConflictType(clientChange.Operation, serverChange.Operation)
        };

        // Add feature data for both versions
        if (clientChange.Feature != null)
        {
            conflict.ClientFeature = clientChange.Feature;
        }

        if (serverChange.Operation != ChangeOperation.Delete)
        {
            var serverFeature = await _featureReader.GetByIdAsync(
                serverChange.LayerId, serverChange.ObjectId, cancellationToken);
            if (serverFeature != null)
            {
                conflict.ServerFeature = ConvertToProtoFeature(serverFeature);
            }
        }

        _statistics.ConflictsDetected++;
        return conflict;
    }

    private void UpdateStatistics(Proto.FeatureOperation operation)
    {
        switch (operation)
        {
            case Proto.FeatureOperation.Add:
                _statistics.FeaturesAdded++;
                break;
            case Proto.FeatureOperation.Update:
                _statistics.FeaturesUpdated++;
                break;
            case Proto.FeatureOperation.Delete:
                _statistics.FeaturesDeleted++;
                break;
        }
    }

    private bool HasPendingConflicts()
    {
        // This would check if there are still unresolved conflicts
        // For now, assume all conflicts are resolved when resolutions are recorded
        return false;
    }

    private static Proto.FeatureOperation ConvertOperation(ChangeOperation operation)
    {
        return operation switch
        {
            ChangeOperation.Create => Proto.FeatureOperation.Add,
            ChangeOperation.Update => Proto.FeatureOperation.Update,
            ChangeOperation.Delete => Proto.FeatureOperation.Delete,
            _ => Proto.FeatureOperation.Update
        };
    }

    private static Proto.ConflictType DetermineConflictType(
        Proto.FeatureOperation clientOp,
        ChangeOperation serverOp)
    {
        return (clientOp, serverOp) switch
        {
            (Proto.FeatureOperation.Update, ChangeOperation.Update) => Proto.ConflictType.UpdateUpdate,
            (Proto.FeatureOperation.Update, ChangeOperation.Delete) => Proto.ConflictType.UpdateDelete,
            (Proto.FeatureOperation.Delete, ChangeOperation.Update) => Proto.ConflictType.DeleteUpdate,
            (Proto.FeatureOperation.Delete, ChangeOperation.Delete) => Proto.ConflictType.DeleteDelete,
            _ => Proto.ConflictType.UpdateUpdate
        };
    }

    private Proto.Feature ConvertToProtoFeature(FeatureRecord feature)
    {
        var protoFeature = new Proto.Feature
        {
            Id = feature.Id
        };

        foreach (var attr in feature.Attributes)
        {
            protoFeature.Attributes[attr.Key] = ConvertAttributeValue(attr.Value);
        }

        return protoFeature;
    }

    private FeatureRecord ConvertFromProtoFeature(Proto.Feature protoFeature)
    {
        var attributes = new Dictionary<string, object?>();

        foreach (var attr in protoFeature.Attributes)
        {
            attributes[attr.Key] = ConvertFromAttributeValue(attr.Value);
        }

        return new FeatureRecord
        {
            Id = protoFeature.Id,
            Attributes = attributes,
            Generation = protoFeature.Metadata?.Generation
        };
    }

    private static Google.Protobuf.WellKnownTypes.Value ConvertAttributeValue(object? value)
    {
        return value switch
        {
            null => Google.Protobuf.WellKnownTypes.Value.ForNull(),
            string s => Google.Protobuf.WellKnownTypes.Value.ForString(s),
            int i => Google.Protobuf.WellKnownTypes.Value.ForNumber(i),
            double d => Google.Protobuf.WellKnownTypes.Value.ForNumber(d),
            bool b => Google.Protobuf.WellKnownTypes.Value.ForBool(b),
            _ => Google.Protobuf.WellKnownTypes.Value.ForString(value.ToString() ?? "")
        };
    }

    private static object? ConvertFromAttributeValue(Google.Protobuf.WellKnownTypes.Value value)
    {
        return value.KindCase switch
        {
            Google.Protobuf.WellKnownTypes.Value.KindOneofCase.NullValue => null,
            Google.Protobuf.WellKnownTypes.Value.KindOneofCase.StringValue => value.StringValue,
            Google.Protobuf.WellKnownTypes.Value.KindOneofCase.NumberValue => value.NumberValue,
            Google.Protobuf.WellKnownTypes.Value.KindOneofCase.BoolValue => value.BoolValue,
            _ => null
        };
    }

    #endregion

    #region Nested Statistics Class

    private class SyncSessionStatistics
    {
        public int FeaturesAdded { get; set; }
        public int FeaturesUpdated { get; set; }
        public int FeaturesDeleted { get; set; }
        public int ConflictsDetected { get; set; }
        public long BytesTransferred { get; set; } = 0; // Would be tracked during actual operations
        public double CompressionRatio { get; set; } = 1.0; // Would be calculated during compression
    }

    #endregion
}