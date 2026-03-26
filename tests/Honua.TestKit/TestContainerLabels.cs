// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.TestKit;

internal static class TestContainerLabels
{
    private const string OwnerKey = "honua.test.owner";
    private const string OwnerValue = "honua-server";
    private const string ResourceKey = "honua.test.resource";
    private const string RunIdKey = "honua.test.run_id";
    private const string RunIdEnv = "HONUA_TEST_RUN_ID";

    public static IReadOnlyDictionary<string, string> For(string resourceName)
    {
        var labels = new Dictionary<string, string>
        {
            [OwnerKey] = OwnerValue,
            [ResourceKey] = resourceName
        };

        var runId = Environment.GetEnvironmentVariable(RunIdEnv);
        if (!string.IsNullOrWhiteSpace(runId))
        {
            labels[RunIdKey] = runId;
        }

        return labels;
    }
}
