// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Admission;

namespace Honua.TestKit.Security;

/// <summary>Explicit synthetic economic values for offline regressions; never production price or term approval.</summary>
public static class AiFinancialTestPolicy
{
    /// <summary>Creates an isolated finite fixture policy for an inert Reporting route.</summary>
    public static AiAdmissionPolicy Create(string destination = "https://fixture.invalid/v1/chat/completions", string model = "fixture-model", string provider = "fixture") => new()
    {
        DeploymentAttempts = 100,
        ActorAttempts = 100,
        CohortAttempts = 100,
        WorkflowAttempts = 100,
        DeploymentConcurrency = 100,
        ActorConcurrency = 100,
        CohortConcurrency = 100,
        WorkflowConcurrency = 100,
        Financial = new()
        {
            Currency = "TST",
            MoneyScale = 1000,
            PeriodAnchorUtc = DateTimeOffset.UnixEpoch,
            PeriodSeconds = 60,
            Deployment = new() { Tokens = 1000, Money = 1000 },
            Actor = new() { Tokens = 1000, Money = 1000 },
            Cohort = new() { Tokens = 1000, Money = 1000 },
            Workflow = new() { Tokens = 1000, Money = 1000 },
            JournalCapacity = 128,
            Basis = new()
            {
                Id = "synthetic-v1",
                SourceDigest = new string('a', 64),
                Provider = provider,
                Kind = "openai",
                BillingSchema = "openai-basic-text-v1",
                ServiceTier = "default",
                UseCase = "reporting-narrative",
                Destination = destination,
                Model = model,
                EffectiveUtc = DateTimeOffset.UnixEpoch,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
                MaximumRequestBytes = 65536,
                MaximumInputTokens = 10,
                MaximumOutputTokens = 5,
                InputRatePerMillion = 1_000_000,
                OutputRatePerMillion = 1_000_000
            }
        }
    };
}
