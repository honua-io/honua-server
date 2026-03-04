// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Proto = Honua.Server.Features.Grpc.Proto.V2;

namespace Honua.Server.Features.Infrastructure.Services;

/// <summary>
/// Production implementation of sync service for managing bidirectional synchronization.
/// Handles conflict detection, resolution, and maintains session state.
/// </summary>
internal sealed class SyncService : ISyncService
{
    private readonly IFeatureReader _featureReader;
    private readonly IFeatureWriter _featureWriter;
    private readonly IChangeTracker _changeTracker;
    private readonly ILogger<SyncService> _logger;

    // Active sync sessions
    private readonly ConcurrentDictionary<string, SyncSession> _activeSessions = new();

    public SyncService(
        IFeatureReader featureReader,
        IFeatureWriter featureWriter,
        IChangeTracker changeTracker,
        ILogger<SyncService> logger)
    {
        _featureReader = featureReader;
        _featureWriter = featureWriter;
        _changeTracker = changeTracker;
        _logger = logger;
    }

    public async Task<ISyncSession> CreateSyncSessionAsync(CancellationToken cancellationToken = default)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var session = new SyncSession(sessionId, _featureReader, _featureWriter, _changeTracker, _logger);

        _activeSessions[sessionId] = session;

        // Clean up session when disposed
        session.SessionDisposed += (s, e) => _activeSessions.TryRemove(sessionId, out _);

        _logger.LogDebug("Created sync session {SessionId}", sessionId);
        return session;
    }

    public async Task<Proto.SyncResponse> ProcessSyncRequestAsync(
        Proto.SyncRequest request,
        ISyncSession session,
        CancellationToken cancellationToken = default)
    {
        using var activity = System.Diagnostics.Activity.Current?.Source.StartActivity("process_sync_request");
        activity?.SetTag("session_id", session.SessionId);
        activity?.SetTag("request_type", request.RequestCase.ToString());

        try
        {
            return request.RequestCase switch
            {
                Proto.SyncRequest.RequestOneofCase.Metadata =>
                    await ProcessSyncMetadataAsync(request.Metadata, session, cancellationToken),

                Proto.SyncRequest.RequestOneofCase.Changes =>
                    await ProcessFeatureChangesAsync(request.Changes, session, cancellationToken),

                Proto.SyncRequest.RequestOneofCase.ConflictResolution =>
                    await ProcessConflictResolutionAsync(request.ConflictResolution, session, cancellationToken),

                Proto.SyncRequest.RequestOneofCase.SyncComplete =>
                    await ProcessSyncCompletionAsync(request.SyncComplete, session, cancellationToken),

                _ => new Proto.SyncResponse
                {
                    Error = new Proto.Error
                    {
                        Code = Proto.ErrorCode.InvalidParameters,
                        Message = $"Unsupported sync request type: {request.RequestCase}"
                    }
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing sync request for session {SessionId}", session.SessionId);
            return new Proto.SyncResponse
            {
                Error = new Proto.Error
                {
                    Code = Proto.ErrorCode.ServiceUnavailable,
                    Message = ex.Message,
                    RequestId = activity?.Id
                }
            };
        }
    }

    public async Task<IEnumerable<Proto.FeatureConflict>> DetectConflictsAsync(
        IEnumerable<Proto.FeatureChange> clientChanges,
        long serverGeneration,
        CancellationToken cancellationToken = default)
    {
        using var activity = System.Diagnostics.Activity.Current?.Source.StartActivity("detect_conflicts");

        var conflicts = new List<Proto.FeatureConflict>();

        foreach (var clientChange in clientChanges)
        {
            // Check if feature has been modified on server since client's last sync
            var serverChanges = await _changeTracker.GetChangesSinceAsync(
                clientChange.LayerId,
                clientChange.BaseGeneration,
                cancellationToken);

            var conflictingChange = serverChanges.FirstOrDefault(sc =>
                sc.ObjectId == clientChange.ObjectId &&
                sc.LayerId == clientChange.LayerId);

            if (conflictingChange != null)
            {
                var conflict = await CreateConflictAsync(clientChange, conflictingChange, cancellationToken);
                conflicts.Add(conflict);
            }
        }

        activity?.SetTag("conflicts_detected", conflicts.Count);
        return conflicts;
    }

    public async Task<Proto.ConflictResolutionResult> ApplyConflictResolutionsAsync(
        IEnumerable<Proto.ConflictResolution> resolutions,
        ISyncSession session,
        CancellationToken cancellationToken = default)
    {
        var results = new List<Proto.ConflictResolutionOutcome>();
        var successCount = 0;
        var failureCount = 0;

        foreach (var resolution in resolutions)
        {
            try
            {
                var outcome = await ApplySingleConflictResolutionAsync(resolution, cancellationToken);
                results.Add(outcome);

                if (outcome.Success)
                    successCount++;
                else
                    failureCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying conflict resolution for feature {FeatureId}",
                    resolution.ConflictId);

                results.Add(new Proto.ConflictResolutionOutcome
                {
                    ConflictId = resolution.ConflictId,
                    Success = false,
                    Error = new Proto.Error
                    {
                        Code = Proto.ErrorCode.ServiceUnavailable,
                        Message = ex.Message
                    }
                });
                failureCount++;
            }
        }

        await session.RecordConflictResolutionsAsync(resolutions, cancellationToken);

        return new Proto.ConflictResolutionResult
        {
            Outcomes = { results },
            SuccessCount = successCount,
            FailureCount = failureCount,
            TotalCount = results.Count
        };
    }

    public async Task<IEnumerable<Proto.FeatureChange>> GetServerChangesSinceAsync(
        string serviceId,
        int layerId,
        long sinceGeneration,
        CancellationToken cancellationToken = default)
    {
        using var activity = System.Diagnostics.Activity.Current?.Source.StartActivity("get_server_changes");
        activity?.SetTag("service_id", serviceId);
        activity?.SetTag("layer_id", layerId.ToString());
        activity?.SetTag("since_generation", sinceGeneration.ToString());

        var changes = await _changeTracker.GetChangesSinceAsync(layerId, sinceGeneration, cancellationToken);

        var protoChanges = new List<Proto.FeatureChange>();
        foreach (var change in changes)
        {
            var protoChange = new Proto.FeatureChange
            {
                ChangeId = change.ChangeId,
                LayerId = change.LayerId,
                ObjectId = change.ObjectId,
                Generation = change.Generation,
                Operation = ConvertOperation(change.Operation),
                ChangeTime = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(change.Timestamp)
            };

            // Include feature data for adds and updates
            if (change.Operation != ChangeOperation.Delete)
            {
                var feature = await _featureReader.GetByIdAsync(layerId, change.ObjectId, cancellationToken);
                if (feature != null)
                {
                    protoChange.Feature = await ConvertToProtoFeatureAsync(feature);
                }
            }

            protoChanges.Add(protoChange);
        }

        activity?.SetTag("changes_returned", protoChanges.Count);
        return protoChanges;
    }

    #region Private Helper Methods

    private async Task<Proto.SyncResponse> ProcessSyncMetadataAsync(
        Proto.SyncMetadata metadata,
        ISyncSession session,
        CancellationToken cancellationToken)
    {
        await session.InitializeAsync(metadata, cancellationToken);

        return new Proto.SyncResponse
        {
            MetadataAck = new Proto.SyncMetadataAck
            {
                SessionId = session.SessionId,
                ServerGeneration = await _changeTracker.GetCurrentGenerationAsync(cancellationToken),
                SupportedConflictStrategies =
                {
                    Proto.ConflictResolutionStrategy.ClientWins,
                    Proto.ConflictResolutionStrategy.ServerWins,
                    Proto.ConflictResolutionStrategy.Manual
                }
            }
        };
    }

    private async Task<Proto.SyncResponse> ProcessFeatureChangesAsync(
        Proto.FeatureChanges changes,
        ISyncSession session,
        CancellationToken cancellationToken)
    {
        var result = await session.ProcessChangesAsync(changes.Changes, cancellationToken);

        return new Proto.SyncResponse
        {
            ChangeResult = result
        };
    }

    private async Task<Proto.SyncResponse> ProcessConflictResolutionAsync(
        Proto.ConflictResolutionBatch resolutionBatch,
        ISyncSession session,
        CancellationToken cancellationToken)
    {
        var result = await ApplyConflictResolutionsAsync(resolutionBatch.Resolutions, session, cancellationToken);

        return new Proto.SyncResponse
        {
            ConflictResult = result
        };
    }

    private async Task<Proto.SyncResponse> ProcessSyncCompletionAsync(
        Proto.SyncComplete complete,
        ISyncSession session,
        CancellationToken cancellationToken)
    {
        var finalGeneration = await session.GetFinalGenerationAsync(cancellationToken);
        var statistics = session.GetStatistics();

        return new Proto.SyncResponse
        {
            Complete = new Proto.SyncComplete
            {
                FinalGeneration = finalGeneration,
                ChangesApplied = statistics.TotalChanges,
                ConflictsResolved = statistics.ConflictsResolved,
                CompletionTime = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow)
            }
        };
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

        // Add feature data for both client and server versions
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
                conflict.ServerFeature = await ConvertToProtoFeatureAsync(serverFeature);
            }
        }

        return conflict;
    }

    private async Task<Proto.ConflictResolutionOutcome> ApplySingleConflictResolutionAsync(
        Proto.ConflictResolution resolution,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (resolution.Strategy)
            {
                case Proto.ConflictResolutionStrategy.ClientWins:
                    await ApplyClientWinsResolutionAsync(resolution, cancellationToken);
                    break;

                case Proto.ConflictResolutionStrategy.ServerWins:
                    // No action needed - server version is already current
                    break;

                case Proto.ConflictResolutionStrategy.Manual:
                    await ApplyManualResolutionAsync(resolution, cancellationToken);
                    break;

                default:
                    throw new ArgumentException($"Unsupported conflict resolution strategy: {resolution.Strategy}");
            }

            return new Proto.ConflictResolutionOutcome
            {
                ConflictId = resolution.ConflictId,
                Success = true,
                AppliedStrategy = resolution.Strategy
            };
        }
        catch (Exception ex)
        {
            return new Proto.ConflictResolutionOutcome
            {
                ConflictId = resolution.ConflictId,
                Success = false,
                Error = new Proto.Error
                {
                    Code = Proto.ErrorCode.ServiceUnavailable,
                    Message = ex.Message
                }
            };
        }
    }

    private async Task ApplyClientWinsResolutionAsync(Proto.ConflictResolution resolution, CancellationToken cancellationToken)
    {
        if (resolution.ResolvedFeature != null)
        {
            var editBatch = new FeatureEditBatch
            {
                Updates = new[] { await ConvertFromProtoFeatureAsync(resolution.ResolvedFeature) }
            };

            await _featureWriter.ApplyEditsAsync(resolution.LayerId, editBatch, cancellationToken);
        }
    }

    private async Task ApplyManualResolutionAsync(Proto.ConflictResolution resolution, CancellationToken cancellationToken)
    {
        if (resolution.ResolvedFeature != null)
        {
            var editBatch = new FeatureEditBatch
            {
                Updates = new[] { await ConvertFromProtoFeatureAsync(resolution.ResolvedFeature) }
            };

            await _featureWriter.ApplyEditsAsync(resolution.LayerId, editBatch, cancellationToken);
        }
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

    private async Task<Proto.Feature> ConvertToProtoFeatureAsync(FeatureRecord feature)
    {
        // Implementation would convert FeatureRecord to Proto.Feature
        // For now, return a basic structure
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

    private async Task<FeatureRecord> ConvertFromProtoFeatureAsync(Proto.Feature protoFeature)
    {
        // Implementation would convert Proto.Feature to FeatureRecord
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
        // Implementation would convert CLR values to protobuf Values
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
}