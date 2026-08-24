using System.Collections.Immutable;
using System.Security.Cryptography;
using NuGet.TestServer.Authentication;
using NuGet.TestServer.Extensions.Sdk;
using NuGet.TestServer.ExternalExtensionTestKit;
using NuGet.TestServer.ForbiddenReferenceFixture;
using NuGet.TestServer.Hosting;

namespace NuGet.TestServer.UnitTests;

/// <summary>
/// Step 20 ("Add trusted third-party in-process loading") package-loader coverage
/// for <see cref="ExternalExtensionConfiguration"/>,
/// <see cref="ExternalExtensionLimits"/>, <see cref="ExternalExtensionPackageLoader"/>,
/// and <see cref="ExternalExtensionRuntime"/>.
///
/// Packages are built in-memory by <see cref="ExternalExtensionPackageBuilder"/>
/// and <see cref="MinimalManifestJson"/> (tests-only helpers with no production
/// references) and staged into temporary "configured root" directories by
/// <see cref="ExternalExtensionRootFixture"/>. The real, separately compiled
/// <c>Contoso.NuTestServer.Flavors</c> fixture
/// (tests/NuGet.TestServer.SdkFixture) supplies the one genuinely loadable entry
/// assembly reused (under different declared package identities where needed)
/// across the whole matrix, so only one `dotnet pack` invocation is required per
/// test run.
/// </summary>
[Collection(nameof(ExternalExtensionAssetsCollection))]
public sealed class ExternalExtensionPackageLoaderTests(ExternalExtensionAssetsFixture fixture)
{
    private ContosoFlavorsAssets Assets => fixture.FlavorsAssets;

    // ---- disabled / default -------------------------------------------------

    [Fact]
    public void Disabled_configuration_has_no_roots_and_is_not_enabled()
    {
        var configuration = ExternalExtensionConfiguration.Disabled;

        Assert.False(configuration.IsEnabled);
        Assert.Empty(configuration.Roots);
        Assert.Empty(configuration.TrustRoots);
    }

    [Fact]
    public void Loading_the_disabled_configuration_yields_no_modules_and_no_diagnostics()
    {
        using var runtime = ExternalExtensionPackageLoader.Load(ExternalExtensionConfiguration.Disabled);

        Assert.Empty(runtime.Modules);
        Assert.Empty(runtime.Diagnostics.Results);
    }

    [Fact]
    public void ServerComposition_omitting_externalExtensions_matches_the_disabled_default()
    {
        var composition = ServerComposition.Create(
            ServerProfiles.Embedded,
            authentication: AuthenticationConfiguration.Anonymous);
        var compositionWithDisabled = ServerComposition.Create(
            ServerProfiles.Embedded,
            authentication: AuthenticationConfiguration.Anonymous,
            externalExtensions: ExternalExtensionConfiguration.Disabled);

        Assert.Equal(
            composition.ExtensionGraph.Routes.Select(route => $"{route.Method} {route.Path}"),
            compositionWithDisabled.ExtensionGraph.Routes.Select(
                route => $"{route.Method} {route.Path}"));
    }

    // ---- valid load ----------------------------------------------------------

    [Fact]
    public void A_correctly_signed_package_from_a_trusted_root_loads_successfully()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "flavors.nupkg",
            ExternalExtensionPackageBuilder.BuildValidPackage(Assets, key));

        using var runtime = ExternalExtensionPackageLoader.Load(Configuration(roots, trustRoot));

        var result = Assert.Single(runtime.Diagnostics.Results);
        Assert.True(result.Succeeded, result.RedactedMessage);
        Assert.Null(result.FailureCode);
        var identity = Assert.IsType<ValidatedExtensionActivationIdentity>(
            result.ActivationIdentity);
        Assert.Equal(Assets.Id, identity.PackageId);
        Assert.Equal(Assets.Id, identity.ManifestId);
        Assert.Equal(Assets.Version, identity.PackageVersion);
        Assert.Equal(Assets.Version, identity.ManifestVersion);
        Assert.Equal(Assets.Publisher, identity.Publisher);
        Assert.Equal(ConformanceAttestationFixture.DefaultKeyId, identity.PublisherKeyId);
        Assert.Matches("^[0-9a-f]{64}$", identity.ManifestDigest);
        Assert.Matches("^[0-9a-f]{64}$", identity.ClosureDigest);
        Assert.Matches("^[0-9a-f]{64}$", identity.StagedContentIdentity);
        Assert.Contains("NuGet.TestServer.SdkFixture", identity.ModuleAssemblyIdentity);
        var module = Assert.Single(runtime.Modules);
        Assert.Equal("Contoso.Flavors", module.Contribution.Manifest.Identity.Id);
    }

    [Theory]
    [InlineData("contoso.flavors", "1.2.3")]
    [InlineData("Contoso.Flavors ", "1.2.3")]
    [InlineData("Contoso.Flavors", "1.2.3.0")]
    public void NuGet_identity_must_ordinally_and_textually_equal_the_manifest_identity(
        string packageId,
        string packageVersion)
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        var packageJson = ExternalExtensionPackageJson.Build(
            Assets.EntryAssemblyFileName,
            Assets.EntryType,
            []);
        var payload = ConformanceAttestationFixture.BuildPayload(
            packageId,
            packageVersion,
            Assets.Publisher,
            ExternalExtensionPackageBuilder.Sha256Hex(Assets.ManifestJsonBytes),
            ExternalExtensionPackageBuilder.StructuralSha256());
        var attestation = ConformanceAttestationFixture.SignToAttestationJson(payload, key);
        roots.WritePackage(
            "identity-mismatch.nupkg",
            ExternalExtensionPackageBuilder.BuildNupkg(
                packageId,
                packageVersion,
                Assets.ManifestJsonBytes,
                packageJson,
                attestation,
                new Dictionary<string, byte[]>
                {
                    [Assets.EntryAssemblyFileName] = Assets.EntryAssemblyBytes
                }));

        using var runtime = ExternalExtensionPackageLoader.Load(Configuration(roots, trustRoot));

        AssertFailed(runtime, "external-extension.package-identity-mismatch");
    }

    [Fact]
    public void Activation_loads_the_exact_entry_assembly_bytes_captured_during_validation()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "flavors.nupkg",
            ExternalExtensionPackageBuilder.BuildValidPackage(Assets, key));

        using var runtime = ExternalExtensionPackageLoader.Load(
            Configuration(roots, trustRoot),
            new ExternalExtensionLoadTestHooks(
                staged => File.WriteAllBytes(staged.EntryAssemblyPath, "mutated"u8.ToArray())));

        Assert.True(Assert.Single(runtime.Diagnostics.Results).Succeeded);
        Assert.Single(runtime.Modules);
    }

    [Fact]
    public void Activation_loads_exact_private_dependency_bytes_captured_during_validation()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "flavors.nupkg",
            ExternalExtensionPackageBuilder.BuildValidPackage(Assets, key));

        using var runtime = ExternalExtensionPackageLoader.Load(
            Configuration(roots, trustRoot),
            new ExternalExtensionLoadTestHooks(
                staged => File.WriteAllBytes(
                    Path.Combine(
                        staged.StageDirectory,
                        "lib",
                        "net10.0",
                        "Contoso.Flavors.Dependency.dll"),
                    "mutated"u8.ToArray())));

        Assert.True(Assert.Single(runtime.Diagnostics.Results).Succeeded);
        Assert.Single(runtime.Modules);
    }

    [Theory]
    [InlineData("extension-manifest.json")]
    [InlineData("extension-package.json")]
    [InlineData("extension-attestation.json")]
    public void Activation_identity_does_not_reopen_validated_root_metadata(string relativePath)
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "flavors.nupkg",
            ExternalExtensionPackageBuilder.BuildValidPackage(Assets, key));

        using var runtime = ExternalExtensionPackageLoader.Load(
            Configuration(roots, trustRoot),
            new ExternalExtensionLoadTestHooks(
                staged => File.WriteAllBytes(
                    Path.Combine(staged.StageDirectory, relativePath),
                    "mutated"u8.ToArray())));

        Assert.True(Assert.Single(runtime.Diagnostics.Results).Succeeded);
        Assert.Single(runtime.Modules);
    }

    // ---- trust root ------------------------------------------------------------

    [Fact]
    public void A_signed_package_with_no_configured_trust_root_is_rejected()
    {
        var (key, _) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "flavors.nupkg",
            ExternalExtensionPackageBuilder.BuildValidPackage(Assets, key));

        using var runtime = ExternalExtensionPackageLoader.Load(
            new ExternalExtensionConfiguration(
                [.. roots.Roots],
                [],
                TimeProvider.System));

        AssertFailed(runtime, "external-extension.trust-root-missing");
    }

    // ---- attestation matrix ----------------------------------------------------

    public static IEnumerable<object[]> AttestationFailureCases()
    {
        yield return ["tampered", "external-extension.attestation-invalid"];
        yield return ["expired", "external-extension.attestation-expired"];
        yield return ["wrong-identity", "external-extension.attestation-invalid"];
        yield return ["wrong-publisher", "external-extension.attestation-invalid"];
        yield return ["wrong-key", "external-extension.attestation-invalid"];
        yield return ["wrong-manifest", "external-extension.attestation-invalid"];
        yield return ["wrong-contract", "external-extension.attestation-invalid"];
        yield return ["wrong-suite", "external-extension.attestation-invalid"];
    }

    [Theory]
    [MemberData(nameof(AttestationFailureCases))]
    public void An_attestation_that_fails_verification_is_rejected(string variant, string expectedFailureCode)
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();

        var nupkg = ExternalExtensionPackageBuilder.BuildValidPackage(Assets, key);
        nupkg = variant switch
        {
            "tampered" => TamperAttestation(nupkg),
            "expired" => ReplaceAttestation(nupkg, key, issuedAt: DateTimeOffset.Parse("2020-01-01T00:00:00Z"),
                expiresAt: DateTimeOffset.Parse("2020-02-01T00:00:00Z")),
            "wrong-identity" => ReplaceAttestation(nupkg, key, packageId: "Contoso.NuTestServer.NotFlavors"),
            "wrong-publisher" => ReplaceAttestation(nupkg, key, publisher: "Fabrikam"),
            "wrong-key" => ReplaceAttestation(
                nupkg,
                ConformanceAttestationFixture.CreateTrustedKey(keyId: ConformanceAttestationFixture.DefaultKeyId).Key),
            "wrong-manifest" => ReplaceAttestation(nupkg, key, manifestSha256: new string('0', 64)),
            "wrong-contract" => ReplaceAttestation(nupkg, key, sdkVersion: new SdkContractVersion(9, 9, 9)),
            "wrong-suite" => ReplaceAttestation(nupkg, key, suite: "not-a-real-suite/v1"),
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null)
        };
        roots.WritePackage("flavors.nupkg", nupkg);

        using var runtime = ExternalExtensionPackageLoader.Load(Configuration(roots, trustRoot));

        AssertFailed(runtime, expectedFailureCode);
    }

    // ---- traversal / duplicate identity ----------------------------------------

    [Fact]
    public void An_entry_name_that_escapes_the_package_root_is_rejected()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        var nupkg = ExternalExtensionPackageBuilder.BuildValidPackage(Assets, key);
        nupkg = ExternalExtensionPackageBuilder.WithEntry(
            nupkg,
            "../../escape.txt",
            "payload"u8.ToArray());
        roots.WritePackage("flavors.nupkg", nupkg);

        using var runtime = ExternalExtensionPackageLoader.Load(Configuration(roots, trustRoot));

        AssertFailed(runtime, "external-extension.path-traversal");
    }

    [Fact]
    public void Archive_entry_paths_that_differ_only_by_case_are_rejected()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        var nupkg = ExternalExtensionPackageBuilder.BuildValidPackage(Assets, key);
        nupkg = ExternalExtensionPackageBuilder.WithEntry(
            nupkg,
            "Extension-Manifest.json",
            "{}"u8.ToArray());
        roots.WritePackage("flavors.nupkg", nupkg);

        using var runtime = ExternalExtensionPackageLoader.Load(Configuration(roots, trustRoot));

        AssertFailed(runtime, "external-extension.path-traversal");
    }

    [Fact]
    public void A_configured_root_that_is_a_symbolic_link_is_rejected()
    {
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        var link = roots.Roots[0] + "-link";
        try
        {
            Directory.CreateSymbolicLink(link, roots.Roots[0]);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return;
        }
        try
        {
            var configuration = new ExternalExtensionConfiguration(
                [link],
                [],
                TimeProvider.System);

            var exception = Assert.Throws<ServerHostingConfigurationException>(
                () => ExternalExtensionPackageLoader.Load(configuration));

            Assert.StartsWith("external-extension.root-reparse-point:", exception.Message);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void Two_packages_with_the_same_identity_differing_only_by_case_collide()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "flavors.nupkg",
            ExternalExtensionPackageBuilder.BuildValidPackage(Assets, key));
        roots.WritePackage(
            "flavors-upper.nupkg",
            ExternalExtensionPackageBuilder.BuildCompanionPackage(
                Assets,
                Assets.Id.ToUpperInvariant(),
                Assets.Version,
                key));

        using var runtime = ExternalExtensionPackageLoader.Load(Configuration(roots, trustRoot));

        Assert.Contains(
            runtime.Diagnostics.Results,
            result => result.FailureCode == "external-extension.duplicate-identity");
    }

    // ---- size / count limits ----------------------------------------------------

    [Fact]
    public void A_package_larger_than_the_configured_maximum_is_rejected()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        var nupkg = ExternalExtensionPackageBuilder.BuildValidPackage(Assets, key);
        roots.WritePackage("flavors.nupkg", nupkg);

        using var runtime = ExternalExtensionPackageLoader.Load(Configuration(
            roots,
            trustRoot,
            limits: new ExternalExtensionLimits(MaximumPackageBytes: nupkg.Length - 1)));

        AssertFailed(runtime, "external-extension.package-too-large");
    }

    [Fact]
    public void An_archive_entry_larger_than_the_configured_maximum_is_rejected()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        var nupkg = ExternalExtensionPackageBuilder.WithEntry(
            ExternalExtensionPackageBuilder.BuildValidPackage(Assets, key),
            "lib/net10.0/oversized.bin",
            new byte[4096]);
        roots.WritePackage("flavors.nupkg", nupkg);

        using var runtime = ExternalExtensionPackageLoader.Load(Configuration(
            roots,
            trustRoot,
            limits: new ExternalExtensionLimits(MaximumEntryBytes: 2048)));

        AssertFailed(runtime, "external-extension.entry-too-large");
    }

    [Fact]
    public void A_highly_compressed_archive_exceeding_the_expanded_limit_is_rejected()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        var nupkg = ExternalExtensionPackageBuilder.WithCompressedEntry(
            ExternalExtensionPackageBuilder.BuildValidPackage(Assets, key),
            "lib/net10.0/compression-bomb.bin",
            new byte[8 * 1024 * 1024]);
        roots.WritePackage("flavors.nupkg", nupkg);

        using var runtime = ExternalExtensionPackageLoader.Load(Configuration(
            roots,
            trustRoot,
            limits: new ExternalExtensionLimits(
                MaximumTotalBytes: 2 * 1024 * 1024,
                MaximumEntryBytes: 9 * 1024 * 1024)));

        AssertFailed(runtime, "external-extension.expanded-size-exceeded");
    }

    [Fact]
    public void More_packages_than_the_configured_count_limit_are_rejected()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "a.nupkg",
            ExternalExtensionPackageBuilder.BuildCompanionPackage(
                Assets, "Contoso.NuTestServer.FlavorsA", "1.0.0", key));
        roots.WritePackage(
            "b.nupkg",
            ExternalExtensionPackageBuilder.BuildCompanionPackage(
                Assets, "Contoso.NuTestServer.FlavorsB", "1.0.0", key));

        using var runtime = ExternalExtensionPackageLoader.Load(Configuration(
            roots,
            trustRoot,
            limits: new ExternalExtensionLimits(MaximumPackageCount: 1)));

        Assert.Contains(
            runtime.Diagnostics.Results,
            result => result.FailureCode == "external-extension.too-many-packages");
    }

    // ---- extension dependency graph ----------------------------------------------

    [Fact]
    public void A_dependency_on_an_extension_not_present_in_any_root_is_rejected()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "flavors.nupkg",
            ExternalExtensionPackageBuilder.BuildValidPackage(
                Assets,
                key,
                extensionDependencies:
                [
                    new ExternalExtensionDependencySpec("Contoso.Missing", "1.0.0", "2.0.0")
                ]));

        using var runtime = ExternalExtensionPackageLoader.Load(Configuration(roots, trustRoot));

        AssertFailed(runtime, "external-extension.dependency-missing");
    }

    [Fact]
    public void A_dependency_whose_declared_range_excludes_the_present_version_is_rejected()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "dependency.nupkg",
            ExternalExtensionPackageBuilder.BuildCompanionPackage(
                Assets, "Contoso.NuTestServer.Dependency", "2.0.0", key));
        roots.WritePackage(
            "flavors.nupkg",
            ExternalExtensionPackageBuilder.BuildValidPackage(
                Assets,
                key,
                extensionDependencies:
                [
                    new ExternalExtensionDependencySpec("Contoso.NuTestServer.Dependency", "1.0.0", "2.0.0")
                ]));

        using var runtime = ExternalExtensionPackageLoader.Load(Configuration(roots, trustRoot));

        AssertFailed(runtime, "external-extension.dependency-range-unsatisfied", Assets.Id);
    }

    [Fact]
    public void A_cycle_between_two_extension_dependencies_is_rejected()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "a.nupkg",
            ExternalExtensionPackageBuilder.BuildCompanionPackage(
                Assets,
                "Contoso.NuTestServer.CycleA",
                "1.0.0",
                key,
                extensionDependencies:
                [
                    new ExternalExtensionDependencySpec("Contoso.NuTestServer.CycleB", "1.0.0", "2.0.0")
                ]));
        roots.WritePackage(
            "b.nupkg",
            ExternalExtensionPackageBuilder.BuildCompanionPackage(
                Assets,
                "Contoso.NuTestServer.CycleB",
                "1.0.0",
                key,
                extensionDependencies:
                [
                    new ExternalExtensionDependencySpec("Contoso.NuTestServer.CycleA", "1.0.0", "2.0.0")
                ]));

        using var runtime = ExternalExtensionPackageLoader.Load(Configuration(roots, trustRoot));

        Assert.Contains(
            runtime.Diagnostics.Results,
            result => result.FailureCode == "external-extension.dependency-cycle");
    }

    [Fact]
    public void Dependency_load_order_is_deterministic_and_topologically_sorted()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        // Written in reverse of dependency order to prove the loader — not the
        // filesystem enumeration order — determines the final load order.
        roots.WritePackage(
            "a.nupkg",
            ExternalExtensionPackageBuilder.BuildCompanionPackage(
                Assets,
                "Contoso.NuTestServer.OrderA",
                "1.0.0",
                key,
                extensionDependencies:
                [
                    new ExternalExtensionDependencySpec("Contoso.NuTestServer.OrderB", "1.0.0", "2.0.0")
                ]));
        roots.WritePackage(
            "b.nupkg",
            ExternalExtensionPackageBuilder.BuildCompanionPackage(
                Assets, "Contoso.NuTestServer.OrderB", "1.0.0", key));

        using var first = ExternalExtensionPackageLoader.Load(Configuration(roots, trustRoot));
        using var second = ExternalExtensionPackageLoader.Load(Configuration(roots, trustRoot));

        Assert.Equal(
            "Contoso.NuTestServer.OrderB",
            Assert.Single(first.Diagnostics.Results).PackageId);
        Assert.Equal(
            "Contoso.NuTestServer.OrderB",
            Assert.Single(second.Diagnostics.Results).PackageId);
        Assert.Empty(first.Modules);
        Assert.Empty(second.Modules);
    }

    // ---- operation / route / resource squatting ------------------------------------

    [Fact]
    public void A_manifest_that_claims_a_kernel_owned_operation_id_is_rejected()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "squat.nupkg",
            ExternalExtensionPackageBuilder.BuildCompanionPackage(
                Assets,
                "Contoso.NuTestServer.OperationSquatter",
                "1.0.0",
                key,
                operationId: OperationIds.ServiceIndexGet));

        using var runtime = ExternalExtensionPackageLoader.Load(Configuration(roots, trustRoot));

        AssertFailed(runtime, "external-extension.operation-squatting");
    }

    [Fact]
    public void A_manifest_that_claims_a_kernel_owned_route_path_is_rejected()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "squat.nupkg",
            ExternalExtensionPackageBuilder.BuildCompanionPackage(
                Assets,
                "Contoso.NuTestServer.RouteSquatter",
                "1.0.0",
                key,
                routeId: "contoso.route-squatter.index",
                routePath: "/v3/index.json"));

        using var runtime = ExternalExtensionPackageLoader.Load(Configuration(roots, trustRoot));

        AssertFailed(runtime, "external-extension.route-squatting");
    }

    [Fact]
    public void A_manifest_that_claims_a_kernel_owned_service_index_resource_kind_is_rejected()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "squat.nupkg",
            ExternalExtensionPackageBuilder.BuildCompanionPackage(
                Assets,
                "Contoso.NuTestServer.ResourceSquatter",
                "1.0.0",
                key,
                resourceId: BuiltInExtensionIds.ServiceIndex));

        using var runtime = ExternalExtensionPackageLoader.Load(Configuration(roots, trustRoot));

        AssertFailed(runtime, "external-extension.resource-squatting");
    }

    // ---- capability enforcement (via ServerComposition, not the loader) -------------

    [Fact]
    public void A_loaded_extension_whose_required_capability_is_not_granted_fails_composition()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "flavors.nupkg",
            ExternalExtensionPackageBuilder.BuildValidPackage(Assets, key));
        using var runtime = ExternalExtensionPackageLoader.Load(Configuration(roots, trustRoot));
        var profile = ServerProfiles.Embedded with
        {
            Extensions =
            [
                .. ServerProfiles.Embedded.Extensions,
                .. runtime.Modules.Select(module => module.Contribution.Selection)
            ]
        };

        var failure = Assert.Throws<ServerHostingConfigurationException>(() =>
            ServerComposition.Create(
                profile,
                authentication: AuthenticationConfiguration.Anonymous,
                modules: [.. runtime.Modules]));

        Assert.Contains("catalog.missing-capability-grant", failure.Message, StringComparison.Ordinal);
        Assert.Contains(BuiltInCapabilityNames.HostClockRead, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_loaded_extensions_optional_capability_does_not_block_composition_when_ungranted()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "flavors.nupkg",
            ExternalExtensionPackageBuilder.BuildValidPackage(Assets, key));
        using var runtime = ExternalExtensionPackageLoader.Load(Configuration(roots, trustRoot));
        var profile = ServerProfiles.Embedded with
        {
            Extensions =
            [
                .. ServerProfiles.Embedded.Extensions,
                .. runtime.Modules.Select(module => module.Contribution.Selection)
            ],
            Grants =
            [
                .. ServerProfiles.Embedded.Grants,
                new CapabilityGrant(BuiltInCapabilityNames.HostClockRead)
            ]
        };

        // Granting only the required host-clock capability (never the optional
        // outbound-http capability the fixture also requests) must still compose.
        var composition = ServerComposition.Create(
            profile,
            authentication: AuthenticationConfiguration.Anonymous,
            modules: [.. runtime.Modules]);

        Assert.Contains(
            composition.ExtensionGraph.Routes,
            route => route.Path == "/flavors/index.json");
    }

    // ---- forbidden references / duplicate SDK identity ------------------------------

    [Fact]
    public void An_entry_assembly_that_references_the_host_kernel_or_official_assembly_is_rejected()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        var forbiddenAssemblyBytes = fixture.ForbiddenReferenceEntryAssemblyBytes;
        var manifestBytes = MinimalManifestJson.Build(
            "Contoso.NuTestServer.Forbidden", "1.0.0", "Contoso");
        var packageJson = ExternalExtensionPackageJson.Build(
            "NuGet.TestServer.ForbiddenReferenceFixture.dll",
            typeof(ForbiddenReferenceExtension).FullName!,
            []);
        var payload = ConformanceAttestationFixture.BuildPayload(
            "Contoso.NuTestServer.Forbidden",
            "1.0.0",
            "Contoso",
            ExternalExtensionPackageBuilder.Sha256Hex(manifestBytes),
            ExternalExtensionPackageBuilder.StructuralSha256());
        var attestation = ConformanceAttestationFixture.SignToAttestationJson(payload, key);
        var nupkg = ExternalExtensionPackageBuilder.BuildNupkg(
            "Contoso.NuTestServer.Forbidden",
            "1.0.0",
            manifestBytes,
            packageJson,
            attestation,
            new Dictionary<string, byte[]>
            {
                ["NuGet.TestServer.ForbiddenReferenceFixture.dll"] = forbiddenAssemblyBytes
            });
        roots.WritePackage("forbidden.nupkg", nupkg);

        using var runtime = ExternalExtensionPackageLoader.Load(Configuration(roots, trustRoot));

        AssertFailed(runtime, "external-extension.forbidden-reference");
    }

    [Fact]
    public void A_package_that_bundles_its_own_private_copy_of_the_sdk_assembly_is_rejected()
    {
        Assert.NotNull(Assets.SdkAssemblyBytes);
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "flavors.nupkg",
            ExternalExtensionPackageBuilder.BuildValidPackage(Assets, key, bundlePrivateSdkCopy: true));

        using var runtime = ExternalExtensionPackageLoader.Load(Configuration(roots, trustRoot));

        AssertFailed(runtime, "external-extension.duplicate-sdk-identity");
    }

    [Fact]
    public void A_package_that_bundles_a_framework_assembly_identity_is_rejected()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        var frameworkAssembly = typeof(Microsoft.Extensions.Hosting.IHostedService).Assembly;
        var nupkg = ExternalExtensionPackageBuilder.WithEntry(
            ExternalExtensionPackageBuilder.BuildValidPackage(Assets, key),
            $"{ExternalExtensionPackageBuilder.LibDirectory}{Path.GetFileName(frameworkAssembly.Location)}",
            File.ReadAllBytes(frameworkAssembly.Location));
        roots.WritePackage("flavors.nupkg", nupkg);

        using var runtime = ExternalExtensionPackageLoader.Load(Configuration(roots, trustRoot));

        AssertFailed(runtime, "external-extension.duplicate-host-assembly");
    }

    // ---- deterministic diagnostics / redaction ---------------------------------------

    [Fact]
    public void Diagnostics_are_ordered_by_package_identity_regardless_of_write_order()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "z.nupkg",
            ExternalExtensionPackageBuilder.BuildCompanionPackage(
                Assets, "Contoso.NuTestServer.Zed", "1.0.0", key));
        roots.WritePackage(
            "a.nupkg",
            ExternalExtensionPackageBuilder.BuildCompanionPackage(
                Assets, "Contoso.NuTestServer.Alpha", "1.0.0", key));

        var untrusted = new ExternalExtensionConfiguration(
            [.. roots.Roots],
            [],
            TimeProvider.System);
        using var first = ExternalExtensionPackageLoader.Load(untrusted);
        using var second = ExternalExtensionPackageLoader.Load(untrusted);

        var expectedOrder = new[] { "Contoso.NuTestServer.Alpha", "Contoso.NuTestServer.Zed" };
        Assert.Equal(expectedOrder, first.Diagnostics.Results.Select(result => result.PackageId));
        Assert.Equal(expectedOrder, second.Diagnostics.Results.Select(result => result.PackageId));
    }

    [Fact]
    public void A_failure_diagnostic_never_leaks_the_configured_root_file_system_path()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "flavors.nupkg",
            ExternalExtensionPackageBuilder.BuildValidPackage(Assets, key));

        using var runtime = ExternalExtensionPackageLoader.Load(
            new ExternalExtensionConfiguration([.. roots.Roots], [], TimeProvider.System));

        var result = Assert.Single(runtime.Diagnostics.Results);
        Assert.False(result.Succeeded);
        Assert.NotNull(result.RedactedMessage);
        Assert.DoesNotContain(roots.Roots[0], result.RedactedMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ---- collectible ALC lifecycle --------------------------------------------------

    [Fact]
    public void Disposing_the_runtime_makes_its_dedicated_load_context_collectible()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "flavors.nupkg",
            ExternalExtensionPackageBuilder.BuildValidPackage(Assets, key));

        var weakAssembly = LoadDisposeAndRelease(Configuration(roots, trustRoot));

        for (var attempt = 0; attempt < 10 && weakAssembly.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(weakAssembly.IsAlive);
    }

    // ---- process-static / parallel host isolation ------------------------------------

    [Fact]
    public void Two_runtimes_loaded_in_parallel_from_independent_roots_share_no_state()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var firstRoots = ExternalExtensionRootFixture.CreateRoots();
        using var secondRoots = ExternalExtensionRootFixture.CreateRoots();
        firstRoots.WritePackage(
            "flavors.nupkg",
            ExternalExtensionPackageBuilder.BuildValidPackage(Assets, key));
        secondRoots.WritePackage(
            "flavors.nupkg",
            ExternalExtensionPackageBuilder.BuildValidPackage(Assets, key));

        using var first = ExternalExtensionPackageLoader.Load(Configuration(firstRoots, trustRoot));
        using var second = ExternalExtensionPackageLoader.Load(Configuration(secondRoots, trustRoot));

        var firstModule = Assert.Single(first.Modules);
        var secondModule = Assert.Single(second.Modules);
        Assert.NotSame(firstModule, secondModule);
        Assert.NotSame(
            Assert.Single(firstModule.Contribution.Contracts).RequestType.Assembly,
            Assert.Single(secondModule.Contribution.Contracts).RequestType.Assembly);

        first.Dispose();

        // The second runtime must be unaffected by disposing the first.
        Assert.Single(second.Modules);
        Assert.NotEmpty(second.Modules[0].Contribution.Manifest.Identity.Id);
    }

    // ---- restart-only snapshot semantics ----------------------------------------------

    [Fact]
    public void A_package_added_after_load_is_invisible_until_the_next_restart_load()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "flavors.nupkg",
            ExternalExtensionPackageBuilder.BuildValidPackage(Assets, key));

        using var initial = ExternalExtensionPackageLoader.Load(Configuration(roots, trustRoot));
        Assert.Single(initial.Modules);

        roots.WritePackage(
            "companion.nupkg",
            ExternalExtensionPackageBuilder.BuildCompanionPackage(
                Assets, "Contoso.NuTestServer.LateArrival", "1.0.0", key));

        // The already-loaded (running) snapshot never observes the new package...
        Assert.Single(initial.Modules);

        // ...but a fresh "restart" load sees it and fails closed because the newly
        // installed package's entry module does not match its declared manifest.
        using var restarted = ExternalExtensionPackageLoader.Load(Configuration(roots, trustRoot));
        Assert.Empty(restarted.Modules);
        Assert.Contains(
            restarted.Diagnostics.Results,
            result => result.FailureCode == "external-extension.module-manifest-mismatch");
    }

    // ---- helpers ---------------------------------------------------------------------

    /// <summary>Referenced directly (not just via `.Load(...)`) so the compiler names
    /// this type explicitly among the missing-type errors that constitute this
    /// phase's red evidence.</summary>
    private static readonly Type LoaderType = typeof(ExternalExtensionPackageLoader);

    private static ExternalExtensionConfiguration Configuration(
        ExternalExtensionRootFixture roots,
        ConformanceTrustRoot trustRoot,
        ExternalExtensionLimits? limits = null) =>
        new(
            [.. roots.Roots],
            [trustRoot],
            TimeProvider.System,
            limits ?? ExternalExtensionLimits.Default);

    private static void AssertFailed(
        ExternalExtensionRuntime runtime,
        string expectedFailureCode,
        string? packageId = null)
    {
        var result = packageId is null
            ? Assert.Single(runtime.Diagnostics.Results)
            : Assert.Single(runtime.Diagnostics.Results, r => r.PackageId == packageId);
        Assert.False(result.Succeeded);
        Assert.Equal(expectedFailureCode, result.FailureCode);
        Assert.Empty(runtime.Modules);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference LoadDisposeAndRelease(
        ExternalExtensionConfiguration configuration)
    {
        var runtime = ExternalExtensionPackageLoader.Load(configuration);
        var module = Assert.Single(runtime.Modules);
        var weakAssembly = new WeakReference(
            Assert.Single(module.Contribution.Contracts).RequestType.Assembly);
        runtime.Dispose();
        return weakAssembly;
    }

    private byte[] TamperAttestation(byte[] nupkg)
    {
        var entries = ExternalExtensionPackageBuilder.ReadEntries(nupkg);
        var tampered = ConformanceAttestationFixture.Tamper(
            entries[ExternalExtensionPackageBuilder.AttestationEntryName]);
        return ExternalExtensionPackageBuilder.WithEntry(
            nupkg,
            ExternalExtensionPackageBuilder.AttestationEntryName,
            tampered);
    }

    private byte[] ReplaceAttestation(
        byte[] nupkg,
        ECDsa key,
        string? packageId = null,
        string? publisher = null,
        string? manifestSha256 = null,
        SdkContractVersion? sdkVersion = null,
        string? suite = null,
        DateTimeOffset? issuedAt = null,
        DateTimeOffset? expiresAt = null)
    {
        var payload = ConformanceAttestationFixture.BuildPayload(
            packageId ?? Assets.Id,
            Assets.Version,
            publisher ?? Assets.Publisher,
            manifestSha256 ?? ExternalExtensionPackageBuilder.Sha256Hex(Assets.ManifestJsonBytes),
            ExternalExtensionPackageBuilder.StructuralSha256(),
            sdkVersion,
            suite);
        var attestation = ConformanceAttestationFixture.SignToAttestationJson(
            payload,
            key,
            issuedAt: issuedAt,
            expiresAt: expiresAt);
        return ExternalExtensionPackageBuilder.WithEntry(
            nupkg,
            ExternalExtensionPackageBuilder.AttestationEntryName,
            attestation);
    }
}

/// <summary>Caches the one expensive `dotnet pack` of the real Contoso Flavors
/// fixture, and the compiled forbidden-reference fixture assembly bytes, across
/// every test in <see cref="ExternalExtensionAssetsCollection"/>.</summary>
public sealed class ExternalExtensionAssetsFixture : IAsyncLifetime
{
    public ContosoFlavorsAssets FlavorsAssets { get; private set; } = null!;

    public byte[] ForbiddenReferenceEntryAssemblyBytes { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        FlavorsAssets = await ExternalExtensionPackageBuilder.BuildContosoFlavorsAssetsAsync();
        ForbiddenReferenceEntryAssemblyBytes = File.ReadAllBytes(
            typeof(ForbiddenReferenceExtension).Assembly.Location);
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

[CollectionDefinition(nameof(ExternalExtensionAssetsCollection))]
public sealed class ExternalExtensionAssetsCollection : ICollectionFixture<ExternalExtensionAssetsFixture>;
