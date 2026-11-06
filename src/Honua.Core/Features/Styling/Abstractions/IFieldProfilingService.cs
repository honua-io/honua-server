// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Domain;

namespace Honua.Core.Features.Styling.Abstractions;

public interface IFieldProfilingService
{
    Task<IReadOnlyList<FieldProfile>> ProfileFieldsAsync(
        int layerId,
        IReadOnlyList<MetadataV2Field> fields,
        int sampleLimit,
        CancellationToken cancellationToken = default);

    Task<double[]> GetNumericValuesAsync(
        int layerId,
        string fieldName,
        int limit,
        CancellationToken cancellationToken = default);
}
