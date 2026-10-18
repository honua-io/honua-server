// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Orchestration.Domain;

/// <summary>Durable command-to-run reference retained independently of expiring run details.</summary>
/// <param name="RunId">Canonical accepted workflow run identity.</param>
/// <param name="RequestHash">Immutable definition/input fingerprint bound to this command.</param>
public sealed record WorkflowRunCommand(string RunId, string RequestHash);
