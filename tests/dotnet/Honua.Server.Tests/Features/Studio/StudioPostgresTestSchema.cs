// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit;

namespace Honua.Server.Tests.Features.Studio;

internal static class StudioPostgresTestSchema
{
    public static async Task ApplyAsync(WebAppFixture fixture)
    {
        var schema = fixture.CurrentSchema ?? throw new InvalidOperationException("Missing fixture schema.");
        await fixture.Postgres.RunUnderSchemaMutationLockAsync(async () =>
        {
            foreach (var file in new[]
                     {
                         "035_CreateStudioPackageLifecycle.sql",
                         "036_CreateContentPublications.sql",
                         "082_CreatePromotionStores.sql",
                         "089_AddStudioContentEnumerationIndexes.sql",
                         "090_AddStudioContentItemOwner.sql",
                         "092_CreateSavedMapOperationLog.sql",
                         "093_EnsureConfiguredStudioPackageLifecycle.sql",
                         "094_CreateSavedMapCheckpointVersionRecovery.sql",
                         "095_DeleteSavedMapOperationLogWithStudioDraft.sql",
                         "120_AddStudioTenantOwnership.sql",
                         "126_AddStudioContentHashInput.sql",
                         "134_VersionCanonicalDeployments.sql",
                     })
            {
                await using var stream = typeof(Program).Assembly.GetManifestResourceStream($"Honua.Server.Migrations.{file}")
                    ?? throw new InvalidOperationException($"Missing embedded migration {file}.");
                using var reader = new StreamReader(stream);
                var sql = (await reader.ReadToEndAsync())
                    .Replace("$HonuaSchema$", $"\"{schema}\"", StringComparison.Ordinal);
                await fixture.Postgres.ExecuteAsync(sql, schema);
            }
        });
    }
}
