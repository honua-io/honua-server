// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Exceptions;

public sealed class InvalidPersistedExportProgressException(Exception? innerException = null)
    : InvalidOperationException("Persisted export progress is invalid. Submit a new export request.", innerException);
