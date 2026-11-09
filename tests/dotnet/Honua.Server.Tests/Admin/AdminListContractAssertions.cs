// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;

namespace Honua.Server.Tests.Admin;

internal static class AdminListContractAssertions
{
    internal static void MatchesPublishedSchema(string path, string payload)
    {
        var document = JObject.Parse(File.ReadAllText(
            RepositoryPaths.Resolve("docs", "developer", "api-specs", "admin-api.json")));
        var responseSchema = document["paths"]![path]!["get"]!["responses"]!["200"]!
            ["content"]!["application/json"]!["schema"]!;
        var schema = new JObject
        {
            ["$ref"] = responseSchema["$ref"]!.DeepClone(),
            ["components"] = document["components"]!.DeepClone()
        };
        var valid = JToken.Parse(payload).IsValid(JSchema.Parse(schema.ToString()), out IList<string> errors);
        Assert.True(valid, string.Join(Environment.NewLine, errors));
    }
}
