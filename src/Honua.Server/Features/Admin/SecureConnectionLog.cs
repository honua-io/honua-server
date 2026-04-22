// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Admin;

internal static partial class SecureConnectionEndpoints
{
    internal static partial class SecureConnectionLog
    {
        [LoggerMessage(EventId = 4405, Level = LogLevel.Warning, Message = "Invalid test connection request: {Errors}")]
        public static partial void InvalidTestConnectionRequest(ILogger logger, string errors);

        [LoggerMessage(EventId = 4406, Level = LogLevel.Warning, Message = "Invalid test connection request: {Error}")]
        public static partial void InvalidTestConnectionRequestError(ILogger logger, string? error);

        [LoggerMessage(EventId = 4407, Level = LogLevel.Error, Message = "Failed to test draft connection")]
        public static partial void TestDraftConnectionFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 4408, Level = LogLevel.Error, Message = "Failed to retrieve secure connections")]
        public static partial void RetrieveSecureConnectionsFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 4409, Level = LogLevel.Warning, Message = "Connection with ID {ConnectionId} not found")]
        public static partial void ConnectionNotFound(ILogger logger, Guid connectionId);

        [LoggerMessage(EventId = 4410, Level = LogLevel.Error, Message = "Failed to retrieve connection {ConnectionId}")]
        public static partial void RetrieveConnectionFailed(ILogger logger, Guid connectionId, Exception exception);

        [LoggerMessage(EventId = 4411, Level = LogLevel.Warning, Message = "Invalid create connection request: {Errors}")]
        public static partial void InvalidCreateConnectionRequest(ILogger logger, string errors);

        [LoggerMessage(EventId = 4412, Level = LogLevel.Warning, Message = "Invalid create connection request: {Error}")]
        public static partial void InvalidCreateConnectionRequestError(ILogger logger, string? error);

        [LoggerMessage(EventId = 4413, Level = LogLevel.Warning, Message = "Failed to create secure connection due to invalid request state")]
        public static partial void CreateSecureConnectionInvalidRequestState(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 4414, Level = LogLevel.Error, Message = "Failed to create secure connection due to internal invalid operation")]
        public static partial void CreateSecureConnectionInvalidOperation(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 4415, Level = LogLevel.Error, Message = "Failed to create secure connection")]
        public static partial void CreateSecureConnectionFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 4416, Level = LogLevel.Error, Message = "Failed to test connection {ConnectionId}")]
        public static partial void TestConnectionFailed(ILogger logger, Guid connectionId, Exception exception);

        [LoggerMessage(EventId = 4417, Level = LogLevel.Error, Message = "Failed to validate encryption service")]
        public static partial void ValidateEncryptionServiceFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 4418, Level = LogLevel.Warning, Message = "Invalid update connection request: {Errors}")]
        public static partial void InvalidUpdateConnectionRequest(ILogger logger, string errors);

        [LoggerMessage(EventId = 4419, Level = LogLevel.Warning, Message = "Connection with ID {ConnectionId} not found for update")]
        public static partial void ConnectionNotFoundForUpdate(ILogger logger, Guid connectionId);

        [LoggerMessage(EventId = 4420, Level = LogLevel.Error, Message = "Failed to update secure connection {ConnectionId}")]
        public static partial void UpdateSecureConnectionFailed(ILogger logger, Guid connectionId, Exception exception);

        [LoggerMessage(EventId = 4421, Level = LogLevel.Warning, Message = "Secure connection {ConnectionId} is in use")]
        public static partial void SecureConnectionInUse(ILogger logger, Guid connectionId, Exception exception);

        [LoggerMessage(EventId = 4422, Level = LogLevel.Error, Message = "Failed to delete secure connection {ConnectionId}")]
        public static partial void DeleteSecureConnectionFailed(ILogger logger, Guid connectionId, Exception exception);

        [LoggerMessage(EventId = 4423, Level = LogLevel.Warning, Message = "Encryption key rotated from {Previous} to {New}")]
        public static partial void EncryptionKeyRotated(ILogger logger, int previous, int @new);

        [LoggerMessage(EventId = 4424, Level = LogLevel.Warning, Message = "Encryption key rotation is not supported")]
        public static partial void EncryptionKeyRotationNotSupported(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 4425, Level = LogLevel.Error, Message = "Failed to rotate encryption key")]
        public static partial void RotateEncryptionKeyFailed(ILogger logger, Exception exception);
    }
}
