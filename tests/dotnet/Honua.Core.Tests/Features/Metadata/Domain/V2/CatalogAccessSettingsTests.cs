// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

public sealed class CatalogAccessSettingsTests
{
    private const string Public = """
        {"resources":[{"metadata":{"id":"a","name":"one"},"accessPolicy":{"allowAnonymous":true,"roles":["viewer"]}},
                      {"metadata":{"id":"b","name":"two"}}]}
        """;

    [UnitTest]
    public void Match_ChangeOutsideAccessSettings_Matches()
        => CatalogAccessSettings.Match(Public, Public.Replace("\"one\"", "\"renamed\"", StringComparison.Ordinal))
            .Should().BeTrue();

    [UnitTest]
    public void Match_ReorderedResourcesAndPolicyKeys_Matches()
    {
        const string reordered = """
            {"resources":[{"metadata":{"id":"b","name":"two"}},
                          {"accessPolicy":{"roles":["viewer"],"allowAnonymous":true},"metadata":{"id":"a","name":"one"}}]}
            """;
        CatalogAccessSettings.Match(Public, reordered).Should().BeTrue();
    }

    [UnitTheory]
    [InlineData("\"allowAnonymous\":true", "\"allowAnonymous\":false")]
    [InlineData("\"roles\":[\"viewer\"]", "\"roles\":[\"viewer\",\"editor\"]")]
    [InlineData("{\"metadata\":{\"id\":\"b\",\"name\":\"two\"}}", "{\"metadata\":{\"id\":\"b\",\"name\":\"two\"},\"permanentFilter\":{\"where\":\"x=1\"}}")]
    [InlineData("{\"metadata\":{\"id\":\"b\",\"name\":\"two\"}}", "{\"metadata\":{\"id\":\"b\",\"name\":\"two\"},\"hidden\":true}")]
    public void Match_AnyAccessSettingChange_DoesNotMatch(string before, string after)
        => CatalogAccessSettings.Match(Public, Public.Replace(before, after, StringComparison.Ordinal))
            .Should().BeFalse();

    [UnitTest]
    public void Match_ResourceAddedWithAccessPolicy_DoesNotMatch()
    {
        var added = Public.Replace("\"name\":\"two\"}}", "\"name\":\"two\"}},{\"metadata\":{\"id\":\"c\"},\"accessPolicy\":{\"allowAnonymous\":true}}", StringComparison.Ordinal);
        CatalogAccessSettings.Match(Public, added).Should().BeFalse();
    }

    // One case per guarded setting, on a resource and on a service, so the guarded list cannot silently shrink.
    [UnitTheory]
    [InlineData("accessPolicy")]
    [InlineData("permanentFilter")]
    [InlineData("permissions")]
    [InlineData("policies")]
    [InlineData("policyIds")]
    [InlineData("ownerEditPolicy")]
    [InlineData("publication")]
    [InlineData("publications")]
    [InlineData("publicationIds")]
    [InlineData("roles")]
    [InlineData("role")]
    [InlineData("tenant")]
    [InlineData("hidden")]
    [InlineData("defaultVisibility")]
    [InlineData("restrictive")]
    [InlineData("status")]
    [InlineData("lifecycle")]
    [InlineData("editing")]
    [InlineData("editable")]
    [InlineData("canModify")]
    [InlineData("capabilities")]
    [InlineData("declaredOperations")]
    [InlineData("protocols")]
    [InlineData("enabledProtocols")]
    [InlineData("enabled")]
    [InlineData("isEnabled")]
    [InlineData("queryable")]
    [InlineData("deleted")]
    [InlineData("retired")]
    public void Match_ChangeToGuardedSetting_DoesNotMatch(string key)
    {
        foreach (var container in new[] { "resources", "services" })
        {
            var before = $"{{\"{container}\":[{{\"id\":\"x\",\"{key}\":1}}]}}";
            var after = $"{{\"{container}\":[{{\"id\":\"x\",\"{key}\":2}}]}}";
            CatalogAccessSettings.Match(before, after).Should().BeFalse("{0} on {1} guards access", key, container);
        }
    }

    [UnitTest]
    public void Match_GuardedFlagMovedBetweenFieldsOrFieldRenamed_DoesNotMatch()
    {
        const string current = """{"fields":[{"name":"notes","hidden":false},{"name":"ssn","hidden":true}]}""";
        const string swapped = """{"fields":[{"name":"ssn","hidden":false},{"name":"notes","hidden":true}]}""";
        const string renamed = """{"fields":[{"name":"notes","hidden":false},{"name":"ssn_last4","hidden":true}]}""";
        CatalogAccessSettings.Match(current, swapped).Should().BeFalse();
        CatalogAccessSettings.Match(current, renamed).Should().BeFalse();
        CatalogAccessSettings.Match(current, """{"fields":[{"name":"SSN","hidden":true},{"name":"notes","hidden":false}]}""")
            .Should().BeTrue("field names compare case-insensitively and order does not matter");
    }

    [UnitTest]
    public void Match_GuardedFlagMovedBetweenUnidentifiedElements_DoesNotMatch()
        => CatalogAccessSettings.Match(
                """{"rules":[{"x":1,"isEnabled":false},{"x":2,"isEnabled":true}]}""",
                """{"rules":[{"x":1,"isEnabled":true},{"x":2,"isEnabled":false}]}""")
            .Should().BeFalse();

    [UnitTest]
    public void Match_StorageBindingRepointed_DoesNotMatch()
        => CatalogAccessSettings.Match(
                """{"storageBindings":[{"id":"b","locator":"public.parcels_redacted"}]}""",
                """{"storageBindings":[{"id":"b","locator":"public.parcels"}]}""")
            .Should().BeFalse();
}
