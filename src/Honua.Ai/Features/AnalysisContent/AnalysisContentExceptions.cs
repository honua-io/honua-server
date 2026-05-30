// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Ai.AnalysisContent;

internal sealed class AnalysisContentValidationException(string message) : Exception(message);

internal sealed class AnalysisContentNotFoundException(string message) : Exception(message);

internal sealed class AnalysisContentConflictException(string message) : Exception(message);
