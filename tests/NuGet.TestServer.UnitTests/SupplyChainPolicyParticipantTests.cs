using System.Collections.Immutable;
using NuGet.TestServer.Authentication;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Extensions.SupplyChain;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel;
using NuGet.TestServer.Kernel.Capabilities;
using NuGet.TestServer.Packages;
using Microsoft.Extensions.DependencyInjection;

namespace NuGet.TestServer.UnitTests;

public sealed class SupplyChainPolicyParticipantTests
{
    [Fact]
    public async Task Deny_overrides_allow_and_abstain_without_registration_order_dependence()
    {
        var participants = new[]
        {
            Registration("advisory", authoritative: false, PolicyDecisionKind.Abstain),
            Registration("allow", authoritative: true, PolicyDecisionKind.Allow),
            Registration("deny", authoritative: true, PolicyDecisionKind.Deny)
        };
        var requirement = new PolicyAggregationRequirement(
            PolicyAggregationKind.DenyOverrides,
            ["allow", "deny"],
            MinimumAuthoritativeParticipants: 2,
            Timeout: TimeSpan.FromSeconds(1));

        var forward = await PolicyParticipantAggregator.EvaluateAsync(
            PolicyPoint,
            Context,
            participants,
            requirement,
            CancellationToken.None);
        var reverse = await PolicyParticipantAggregator.EvaluateAsync(
            PolicyPoint,
            Context,
            participants.Reverse(),
            requirement,
            CancellationToken.None);

        Assert.Equal(PolicyDecisionKind.Deny, forward.Decision.Kind);
        Assert.Equal("denied", forward.Decision.ReasonCode);
        Assert.Equal(forward.Decision, reverse.Decision);
        Assert.Equal(
            forward.Results.Select(result => result.ParticipantId),
            reverse.Results.Select(result => result.ParticipantId));
        Assert.Equal(["advisory", "allow", "deny"], forward.Results.Select(result => result.ParticipantId));
    }

    [Fact]
    public async Task Missing_zero_or_failed_authoritative_participants_fail_closed()
    {
        var requirement = new PolicyAggregationRequirement(
            PolicyAggregationKind.AllMustAllow,
            ["signature", "scanner"],
            MinimumAuthoritativeParticipants: 2,
            Timeout: TimeSpan.FromSeconds(1));

        var empty = await PolicyParticipantAggregator.EvaluateAsync(
            PolicyPoint,
            Context,
            [],
            requirement,
            CancellationToken.None);
        var missing = await PolicyParticipantAggregator.EvaluateAsync(
            PolicyPoint,
            Context,
            [Registration("signature", authoritative: true, PolicyDecisionKind.Allow)],
            requirement,
            CancellationToken.None);
        var failed = await PolicyParticipantAggregator.EvaluateAsync(
            PolicyPoint,
            Context,
            [
                Registration("signature", authoritative: true, PolicyDecisionKind.Allow),
                new PolicyParticipantRegistration<SupplyChainPolicyContext>(
                    PolicyPoint,
                    "scanner",
                    IsAuthoritative: true,
                    new ThrowingParticipant())
            ],
            requirement,
            CancellationToken.None);

        Assert.All([empty, missing, failed], result =>
        {
            Assert.Equal(PolicyDecisionKind.Deny, result.Decision.Kind);
            Assert.True(result.FailedClosed);
        });
        Assert.Equal("policy.required-participant-missing", empty.Decision.ReasonCode);
        Assert.Equal("policy.required-participant-missing", missing.Decision.ReasonCode);
        Assert.Equal("policy.participant-failed", failed.Decision.ReasonCode);
    }

    [Fact]
    public async Task Participant_timeout_fails_closed_but_caller_cancellation_propagates()
    {
        var registration = new PolicyParticipantRegistration<SupplyChainPolicyContext>(
            PolicyPoint,
            "scanner",
            IsAuthoritative: true,
            new BlockingParticipant());
        var timeoutRequirement = new PolicyAggregationRequirement(
            PolicyAggregationKind.AllMustAllow,
            ["scanner"],
            MinimumAuthoritativeParticipants: 1,
            Timeout: TimeSpan.FromMilliseconds(25));

        var timedOut = await PolicyParticipantAggregator.EvaluateAsync(
            PolicyPoint,
            Context,
            [registration],
            timeoutRequirement,
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PolicyParticipantAggregator.EvaluateAsync(
                PolicyPoint,
                Context,
                [registration],
                timeoutRequirement with { Timeout = TimeSpan.FromSeconds(10) },
                cancellation.Token).AsTask());

        Assert.Equal(PolicyDecisionKind.Deny, timedOut.Decision.Kind);
        Assert.Equal("policy.participant-timeout", timedOut.Decision.ReasonCode);
        Assert.True(timedOut.FailedClosed);
    }

    [Fact]
    public async Task Official_participants_evaluate_typed_facts_without_authoritative_mutation_access()
    {
        var inspection = new StubInspectionCapability(
            new PackageSignatureInspection(SignatureInspectionOutcome.Unsigned, "unsigned"),
            new PackageScannerInspection(PackageScannerInspectionOutcome.Clean, "clean"));
        var extension = new SupplyChainExtension();
        var registry = new RecordingPolicyParticipantRegistry();
        extension.RegisterPolicyParticipants(
            registry,
            new StubExtensionCapabilities(inspection));

        var context = Context with
        {
            RequireSignedPackage = true,
            ExistingOwner = "owner",
            IdentityOwnsPackage = true,
            NamespaceAllowed = true,
            QuotaAvailable = true
        };
        var decisions = await Task.WhenAll(registry.Participants.Select(async participant =>
            (participant.ParticipantId, Decision: await participant.Participant.EvaluateAsync(
                context,
                CancellationToken.None))));

        Assert.Contains(decisions, result =>
            result.ParticipantId == SupplyChainPolicyParticipantIds.Signature &&
            result.Decision.Kind == PolicyDecisionKind.Deny);
        Assert.Contains(decisions, result =>
            result.ParticipantId == SupplyChainPolicyParticipantIds.Scanner &&
            result.Decision.Kind == PolicyDecisionKind.Allow);
        Assert.All(
            typeof(IPackageSignatureInspectionCapability).GetMethods()
                .Concat(typeof(IPackageScannerCapability).GetMethods()),
            method => Assert.DoesNotContain(
                method.GetParameters(),
                parameter => parameter.ParameterType.Namespace?.StartsWith(
                    "NuGet.TestServer.Packages",
                    StringComparison.Ordinal) == true));
    }

    [Fact]
    public void Production_rejects_missing_authoritative_participants_before_readiness()
    {
        using var storage = new TemporaryDirectory();
        var invalid = ServerProfiles.Production with
        {
            PolicyRequirements =
            [
                new ProfilePolicyRequirement(
                    SupplyChainPolicyPoints.Publication,
                    [SupplyChainPolicyParticipantIds.Signature, "missing"],
                    MinimumAuthoritativeParticipants: 2)
            ]
        };

        var exception = Assert.Throws<ServerHostingConfigurationException>(() =>
            ServerComposition.Create(
                invalid,
                storageDirectory: storage.Path,
                authentication: AuthenticationConfiguration.Create(
                    username: null,
                    password: null,
                    apiKey: "publish-key"),
                supplyChain: new Packages.SupplyChainOptions()));

        Assert.Contains("authoritative", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Readiness_rejects_authoritative_participant_registered_for_wrong_context()
    {
        var module = new EmptyPolicyModule();
        var profile = ServerProfiles.Embedded with
        {
            Extensions = ServerProfiles.Embedded.Extensions.Add(module.Contribution.Selection),
            PolicyRequirements =
            [
                new ProfilePolicyRequirement(
                    EmptyPolicyModule.PolicyPoint,
                    [EmptyPolicyModule.ParticipantId],
                    MinimumAuthoritativeParticipants: 1)
            ]
        };
        var composition = ServerComposition.Create(profile, modules: [module]);

        var exception = Assert.Throws<ServerHostingConfigurationException>(
            () => ServerApplication.Build(composition));

        Assert.Contains(
            "missing-active-authoritative-policy-participant",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Official_inspection_capability_is_attributed_and_cannot_mutate_authority()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);
        var service = host.Services.GetRequiredService<PackageSupplyChainService>();

        var publication = await service.PublishAsync(new(
            TestPackageBuilder.Create("Policy.Audit", "1.0.0").Build(),
            "owner",
            "repository"));

        Assert.Equal(PackagePublicationOutcome.Published, publication.Outcome);
        var entries = host.Services.GetRequiredService<CapabilityAuditLog>().Entries
            .Where(entry =>
                entry.OwnerId == SupplyChainExtension.ExtensionId &&
                entry.CapabilityName is KernelCapabilityNames.SupplyChainSignatureInspect or
                    KernelCapabilityNames.SupplyChainPackageScan)
            .ToArray();
        Assert.Equal(["inspect-signature", "scan"], entries
            .Select(entry => entry.Action)
            .Order(StringComparer.Ordinal));
        Assert.All(entries, entry => Assert.Equal(CapabilityCallOutcome.Succeeded, entry.Outcome));
        Assert.DoesNotContain(
            typeof(IPackageSignatureInspectionCapability).GetMethods()
                .Concat(typeof(IPackageScannerCapability).GetMethods()),
            method => method.Name.Contains("Write", StringComparison.Ordinal) ||
                      method.Name.Contains("Publish", StringComparison.Ordinal) ||
                      method.Name.Contains("Moderate", StringComparison.Ordinal));
    }

    private const string PolicyPoint = "test.publication";

    private static readonly SupplyChainPolicyContext Context = new(
        new PolicyPackageHandle("package-handle"),
        new PolicyPackageIdentity("Package", "1.0.0"),
        ContentLength: 42,
        IdentityName: "owner",
        Repository: "repository",
        Administrator: false,
        RequireSignedPackage: false,
        ExistingOwner: null,
        IdentityOwnsPackage: true,
        NamespaceAllowed: true,
        QuotaAvailable: true);

    private static PolicyParticipantRegistration<SupplyChainPolicyContext> Registration(
        string id,
        bool authoritative,
        PolicyDecisionKind decision) =>
        new(
            PolicyPoint,
            id,
            authoritative,
            new FixedParticipant(new PolicyDecision(
                decision,
                decision == PolicyDecisionKind.Deny ? "denied" : null)));

    private sealed class FixedParticipant(PolicyDecision decision)
        : IPolicyParticipant<SupplyChainPolicyContext>
    {
        public ValueTask<PolicyDecision> EvaluateAsync(
            SupplyChainPolicyContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(decision);
        }
    }

    private sealed class ThrowingParticipant : IPolicyParticipant<SupplyChainPolicyContext>
    {
        public ValueTask<PolicyDecision> EvaluateAsync(
            SupplyChainPolicyContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<PolicyDecision>(new InvalidOperationException("failed"));
    }

    private sealed class BlockingParticipant : IPolicyParticipant<SupplyChainPolicyContext>
    {
        public async ValueTask<PolicyDecision> EvaluateAsync(
            SupplyChainPolicyContext context,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new PolicyDecision(PolicyDecisionKind.Allow, null);
        }
    }

    private sealed class StubInspectionCapability(
        PackageSignatureInspection signature,
        PackageScannerInspection scanner) :
        IPackageSignatureInspectionCapability,
        IPackageScannerCapability
    {
        public ValueTask<PackageSignatureInspection> InspectSignatureAsync(
            PolicyPackageHandle package,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(signature);

        public ValueTask<PackageScannerInspection> ScanAsync(
            PolicyPackageHandle package,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(scanner);
    }

    private sealed class StubExtensionCapabilities(
        StubInspectionCapability inspection) : IExtensionCapabilities
    {
        public ImmutableHashSet<string> GrantedCapabilities { get; } =
        [
            KernelCapabilityNames.SupplyChainSignatureInspect,
            KernelCapabilityNames.SupplyChainPackageScan
        ];

        public TCapability GetRequired<TCapability>(string capabilityName)
            where TCapability : class
        {
            if (!GrantedCapabilities.Contains(capabilityName))
            {
                throw new InvalidOperationException(capabilityName);
            }

            return (TCapability)(object)inspection;
        }

        public bool TryGet<TCapability>(string capabilityName, out TCapability? capability)
            where TCapability : class
        {
            capability = GrantedCapabilities.Contains(capabilityName)
                ? (TCapability)(object)inspection
                : null;
            return capability is not null;
        }
    }

    private sealed class RecordingPolicyParticipantRegistry : IPolicyParticipantRegistry
    {
        public List<PolicyParticipantRegistration<SupplyChainPolicyContext>> Participants { get; } = [];

        public IPolicyParticipantRegistry Register<TContext>(
            string extensionId,
            PolicyParticipantRegistration<TContext> participant)
        {
            Assert.Equal(SupplyChainExtension.ExtensionId, extensionId);
            Participants.Add(Assert.IsType<PolicyParticipantRegistration<SupplyChainPolicyContext>>(
                participant));
            return this;
        }
    }

    private sealed class EmptyPolicyModule : IExtensionModule
    {
        public const string PolicyPoint = "test.empty-policy";
        public const string ParticipantId = "test.empty-participant";

        public ExtensionModuleContribution Contribution { get; } = new(
            new ExtensionManifest(
                1,
                "test.empty-policy-module",
                new ExtensionVersion(1, 0, 0),
                ExtensionVersionRange.Major(1),
                [],
                [],
                [],
                [],
                []),
            [])
        {
            PolicyParticipants =
            [
                new PolicyParticipantDescriptor(
                        PolicyPoint,
                        ParticipantId,
                        IsAuthoritative: true)
            ]
        };

        public void RegisterOperations(
            IOperationOwnerRegistry registry,
            IExtensionCapabilities capabilities,
            IDocumentContributionSource documentContributions)
        {
        }

        public void RegisterPolicyParticipants(
            IPolicyParticipantRegistry registry,
            IExtensionCapabilities capabilities) =>
            registry.Register(
                Contribution.Manifest.Id,
                new PolicyParticipantRegistration<string>(
                    PolicyPoint,
                    ParticipantId,
                    IsAuthoritative: true,
                    new StringPolicyParticipant()));

        private sealed class StringPolicyParticipant : IPolicyParticipant<string>
        {
            public ValueTask<PolicyDecision> EvaluateAsync(
                string context,
                CancellationToken cancellationToken) =>
                ValueTask.FromResult(new PolicyDecision(PolicyDecisionKind.Allow, null));
        }

    }
}
