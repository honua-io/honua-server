// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

[Protocol(ProtocolNames.TestQuality)]
public sealed class MetadataV2GovernanceExtensionsTests
{
    [UnitTest]
    [Operation(Operations.Metadata)]
    public void WithServiceGovernanceFallbacks_ResourceLicense_SuppressesServiceLicenseLink()
    {
        var resourceSelfLink = new MetadataV2Link
        {
            Href = "https://example.test/resource",
            Rel = "self",
        };
        var serviceLicenseLink = new MetadataV2Link
        {
            Href = "https://example.test/service-license",
            Rel = "license",
        };
        var serviceDescriptionLink = new MetadataV2Link
        {
            Href = "https://example.test/service-description",
            Rel = "describedby",
        };
        var resourceMetadata = new MetadataV2ObjectMetadata
        {
            License = "proprietary",
            Links = [resourceSelfLink],
        };
        var serviceMetadata = new MetadataV2ObjectMetadata
        {
            License = "MIT",
            Links = [serviceLicenseLink, serviceDescriptionLink],
        };

        var effective = resourceMetadata.WithServiceGovernanceFallbacks(serviceMetadata);

        effective.License.Should().Be("proprietary");
        effective.Links.Should().Equal(resourceSelfLink, serviceDescriptionLink);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void WithServiceGovernanceFallbacks_ResourceLicenseLink_SuppressesServiceLicenseLink()
    {
        var resourceLicenseLink = new MetadataV2Link
        {
            Href = "https://example.test/resource-license",
            Rel = "license",
        };
        var serviceLicenseLink = new MetadataV2Link
        {
            Href = "https://example.test/service-license",
            Rel = "license",
        };
        var resourceMetadata = new MetadataV2ObjectMetadata { Links = [resourceLicenseLink] };
        var serviceMetadata = new MetadataV2ObjectMetadata
        {
            License = "MIT",
            Links = [serviceLicenseLink],
        };

        var effective = resourceMetadata.WithServiceGovernanceFallbacks(serviceMetadata);

        effective.Links.Should().ContainSingle().Which.Should().BeSameAs(resourceLicenseLink);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void WithServiceGovernanceFallbacks_MissingResourceLicense_InheritsServiceLicenseLink()
    {
        var serviceLicenseLink = new MetadataV2Link
        {
            Href = "https://example.test/service-license",
            Rel = "license",
        };
        var serviceMetadata = new MetadataV2ObjectMetadata
        {
            License = "MIT",
            Links = [serviceLicenseLink],
        };

        var effective = new MetadataV2ObjectMetadata()
            .WithServiceGovernanceFallbacks(serviceMetadata);

        effective.License.Should().Be("MIT");
        effective.Links.Should().ContainSingle().Which.Should().BeSameAs(serviceLicenseLink);
    }
}
