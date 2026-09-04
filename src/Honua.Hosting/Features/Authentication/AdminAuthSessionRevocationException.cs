// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

internal sealed class AdminAuthSessionRevocationException(Exception innerException)
    : Exception("The shared admin session could not be revoked.", innerException);
