// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FileImport.Domain;

/// <summary>
/// The background import queue has reached its configured concurrency and depth cap.
/// Endpoints map this to HTTP 429.
/// </summary>
public sealed class ImportJobQueueFullException : Exception
{
    public ImportJobQueueFullException()
        : base("The import job queue is full.")
    {
    }

    public ImportJobQueueFullException(string message)
        : base(message)
    {
    }

    public ImportJobQueueFullException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
