using System.Text;
using System.Text.Json.Nodes;
using NuExtVault.Extensions.Sdk;

namespace NuExtVault.Extensions.Sdk.Tests;

public sealed class ManifestContractTests
{
    [Fact]
    public void Strict_validator_accepts_the_v1_manifest()
    {
        var bytes = File.ReadAllBytes(TestPaths.Fixture("valid-v1.manifest.json"));

        var result = ExtensionManifestJson.Validate(bytes);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.NotNull(result.Manifest);
        Assert.Equal("Contoso.Flavors", result.Manifest.Identity.Id);
        Assert.Equal("1.2.3", result.Manifest.Identity.Version);
        Assert.Equal("Contoso", result.Manifest.Identity.Publisher);
        Assert.Equal(new ManifestSchemaVersion(1), result.Manifest.SchemaVersion);
        Assert.Equal(new SdkContractVersion(1, 0, 0), result.Manifest.Sdk.Minimum);
        Assert.Equal(new SdkContractVersion(2, 0, 0), result.Manifest.Sdk.MaximumExclusive);
        Assert.Equal(CapabilityRequirement.Required, result.Manifest.Capabilities[0].Requirement);
        Assert.Equal(CapabilityRequirement.Optional, result.Manifest.Capabilities[1].Requirement);
    }

    [Theory]
    [InlineData("malformed.manifest.json", "json.invalid")]
    [InlineData("missing-required.manifest.json", "manifest.required")]
    [InlineData("unknown-member.manifest.json", "manifest.unknown-member")]
    [InlineData("unsupported-schema.manifest.json", "manifest.schema.unsupported")]
    [InlineData("unsupported-sdk.manifest.json", "manifest.sdk.unsupported")]
    [InlineData("invalid-version.manifest.json", "manifest.version.invalid")]
    [InlineData("duplicate-identity.manifest.json", "manifest.identity.duplicate")]
    [InlineData("implicit-capability.manifest.json", "manifest.capability.requirement-required")]
    [InlineData("unknown-nested-member.manifest.json", "manifest.unknown-member")]
    [InlineData("replacement-enabled.manifest.json", "operation.replacement.disabled")]
    public void Strict_validator_rejects_invalid_unknown_schema_and_version_cases(
        string fixture,
        string expectedCode)
    {
        var result = ExtensionManifestJson.Validate(File.ReadAllBytes(TestPaths.Fixture(fixture)));

        Assert.False(result.IsValid);
        Assert.Null(result.Manifest);
        Assert.Contains(result.Errors, error => error.Code == expectedCode);
        Assert.Equal(
            result.Errors.OrderBy(error => error.Path, StringComparer.Ordinal)
                .ThenBy(error => error.Code, StringComparer.Ordinal),
            result.Errors);
    }

    [Fact]
    public void Validation_is_deterministic_and_does_not_depend_on_json_member_order()
    {
        var first = ExtensionManifestJson.Validate(
            File.ReadAllBytes(TestPaths.Fixture("valid-v1.manifest.json")));
        var second = ExtensionManifestJson.Validate(
            File.ReadAllBytes(TestPaths.Fixture("valid-v1.reordered.manifest.json")));

        Assert.True(first.IsValid);
        Assert.True(second.IsValid);
        Assert.Equal(first.Manifest, second.Manifest);
        Assert.Equal(first.Errors, second.Errors);
    }

    [Fact]
    public void Canonical_manifest_bytes_and_digest_are_frozen()
    {
        var parsed = ExtensionManifestJson.Parse(
            File.ReadAllBytes(TestPaths.Fixture("valid-v1.reordered.manifest.json")));
        var expected = File.ReadAllBytes(TestPaths.Fixture("valid-v1.canonical.json"));

        var canonical = ExtensionManifestJson.Canonicalize(parsed);
        var digest = ExtensionManifestJson.ComputeDigest(parsed);

        Assert.Equal(expected, canonical.ToArray());
        Assert.Equal(Encoding.UTF8.GetString(expected), Encoding.UTF8.GetString(canonical.Span));
        Assert.Equal(
            File.ReadAllText(TestPaths.Fixture("valid-v1.canonical.sha256")).Trim(),
            digest.Hex);
        Assert.Equal(TestPaths.Sha256(expected), digest.Hex);
        Assert.Equal(digest, ExtensionManifestJson.ComputeDigest(canonical));
    }

    [Fact]
    public void Canonicalization_is_idempotent_and_orders_identity_sets_ordinally()
    {
        var manifest = ExtensionManifestJson.Parse(
            File.ReadAllBytes(TestPaths.Fixture("valid-v1.reordered.manifest.json")));

        var once = ExtensionManifestJson.Canonicalize(manifest);
        var twice = ExtensionManifestJson.Canonicalize(
            ExtensionManifestJson.Parse(once));

        Assert.Equal(once.ToArray(), twice.ToArray());
        Assert.True(
            once.Span.IndexOf("\"host.clock.read\""u8) <
            once.Span.IndexOf("\"network.outbound-http\""u8));
        Assert.False(once.Span.EndsWith("\n"u8));
    }

    [Fact]
    public void Identity_predecessors_are_validated_and_signed_by_canonicalization()
    {
        var root = JsonNode.Parse(
            File.ReadAllBytes(TestPaths.Fixture("valid-v1.manifest.json")))!.AsObject();
        root["$schema"] = "https://schemas.nuextvault.dev/extensions/manifest/v2";
        root["schemaVersion"] = 2;
        root["contracts"]!["manifest"] = 2;
        root["sdk"]!["minimum"] = "1.4.0";
        root["identityPredecessors"] = new JsonArray("Contoso.Legacy");

        var manifest = ExtensionManifestJson.Parse(Encoding.UTF8.GetBytes(root.ToJsonString()));
        var canonical = Encoding.UTF8.GetString(
            ExtensionManifestJson.Canonicalize(manifest).Span);

        Assert.Equal(["Contoso.Legacy"], manifest.IdentityPredecessors.ToArray());
        Assert.Contains(
            "\"identityPredecessors\":[\"Contoso.Legacy\"]",
            canonical,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""["Contoso.Legacy"]""")]
    public void Manifest_v1_rejects_the_identity_lineage_member(string predecessors)
    {
        var root = JsonNode.Parse(
            File.ReadAllBytes(TestPaths.Fixture("valid-v1.manifest.json")))!.AsObject();
        root["identityPredecessors"] = JsonNode.Parse(predecessors);

        var result = ExtensionManifestJson.Validate(Encoding.UTF8.GetBytes(root.ToJsonString()));

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Code == "manifest.identity-predecessor.schema-required");
    }

    [Fact]
    public void Manifest_v2_requires_an_sdk_version_newer_than_the_v1_host_contract()
    {
        var root = JsonNode.Parse(
            File.ReadAllBytes(TestPaths.Fixture("valid-v1.manifest.json")))!.AsObject();
        root["$schema"] = "https://schemas.nuextvault.dev/extensions/manifest/v2";
        root["schemaVersion"] = 2;
        root["contracts"]!["manifest"] = 2;
        root["sdk"]!["minimum"] = "1.3.0";

        var result = ExtensionManifestJson.Validate(Encoding.UTF8.GetBytes(root.ToJsonString()));

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Code == "manifest.sdk.minimum-required");
    }

    [Theory]
    [InlineData("""["Contoso.Flavors"]""", "manifest.identity-predecessor.self")]
    [InlineData(
        """["Contoso.Legacy","contoso.legacy"]""",
        "manifest.identity-predecessor.duplicate")]
    public void Identity_predecessors_reject_self_links_and_duplicates(
        string predecessors,
        string expectedCode)
    {
        var root = JsonNode.Parse(
            File.ReadAllBytes(TestPaths.Fixture("valid-v1.manifest.json")))!.AsObject();
        root["$schema"] = "https://schemas.nuextvault.dev/extensions/manifest/v2";
        root["schemaVersion"] = 2;
        root["contracts"]!["manifest"] = 2;
        root["sdk"]!["minimum"] = "1.4.0";
        root["identityPredecessors"] = JsonNode.Parse(predecessors);

        var result = ExtensionManifestJson.Validate(
            Encoding.UTF8.GetBytes(root.ToJsonString()));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == expectedCode);
    }
}
