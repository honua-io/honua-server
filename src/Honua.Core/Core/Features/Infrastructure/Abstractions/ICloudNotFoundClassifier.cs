// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Abstractions;

public interface ICloudNotFoundClassifier
{
    // An unfamiliar exception type returns false instead of throwing, so classifiers can be chained.
    bool IsNotFound(Exception exception);
}
