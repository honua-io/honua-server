// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Watermark;

// The mark is read before a pull and advanced only after that pull succeeds, so a failed run resumes from the last durable mark.
public interface ISourceWatermarkStore
{
    Task<SourceWatermark?> GetAsync(
        string pipelineId,
        string sourceId,
        CancellationToken cancellationToken = default);

    // A timestamp watermark never moves backward, so a late completion cannot make the next run re-pull extracted records.
    Task<SourceWatermark> AdvanceAsync(
        SourceWatermark watermark,
        CancellationToken cancellationToken = default);

    Task ClearAsync(
        string pipelineId,
        string sourceId,
        CancellationToken cancellationToken = default);
}
