using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Sdk;

namespace NuGet.TestServer.Extensions.SupplyChain;

internal sealed class SupplyChainExtension : IExtensionModule
{
    public const string ExtensionId = BuiltInExtensionIds.SupplyChainPolicy;

    private static readonly ImmutableArray<PolicyParticipantDescriptor> Participants =
    [
        Descriptor(SupplyChainPolicyPoints.Validation, SupplyChainPolicyParticipantIds.Signature),
        Descriptor(SupplyChainPolicyPoints.Validation, SupplyChainPolicyParticipantIds.Scanner),
        Descriptor(SupplyChainPolicyPoints.Admission, SupplyChainPolicyParticipantIds.Ownership),
        Descriptor(SupplyChainPolicyPoints.Admission, SupplyChainPolicyParticipantIds.Namespace),
        Descriptor(SupplyChainPolicyPoints.Admission, SupplyChainPolicyParticipantIds.Quota)
    ];

    public ExtensionModuleContribution Contribution { get; } = new(
        new ExtensionManifest(
            1,
            ExtensionId,
            new ExtensionVersion(1, 0, 0),
            ExtensionVersionRange.Major(1),
            [],
            [],
            [],
            [],
            [
                new CapabilityRequest(
                    KernelCapabilityNames.SupplyChainSignatureInspect,
                    true),
                new CapabilityRequest(
                    KernelCapabilityNames.SupplyChainPackageScan,
                    true)
            ]),
        [])
    {
        PolicyParticipants = Participants
    };

    public void RegisterOperations(
        IOperationOwnerRegistry registry,
        IExtensionCapabilities capabilities,
        IDocumentContributionSource documentContributions)
    {
    }

    public void RegisterPolicyParticipants(
        IPolicyParticipantRegistry registry,
        IExtensionCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(capabilities);
        Register(
            registry,
            new SignaturePolicyParticipant(
                capabilities.GetRequired<IPackageSignatureInspectionCapability>(
                    KernelCapabilityNames.SupplyChainSignatureInspect)));
        Register(
            registry,
            new ScannerPolicyParticipant(
                capabilities.GetRequired<IPackageScannerCapability>(
                    KernelCapabilityNames.SupplyChainPackageScan)));
        Register(registry, new OwnershipPolicyParticipant());
        Register(registry, new NamespacePolicyParticipant());
        Register(registry, new QuotaPolicyParticipant());
    }

    private static PolicyParticipantDescriptor Descriptor(string point, string id) =>
        new(point, id, IsAuthoritative: true);

    private static void Register(
        IPolicyParticipantRegistry registry,
        ISupplyChainPolicyParticipant participant) =>
        registry.Register(
            ExtensionId,
            new PolicyParticipantRegistration<SupplyChainPolicyContext>(
                participant.Id is SupplyChainPolicyParticipantIds.Signature or
                    SupplyChainPolicyParticipantIds.Scanner
                    ? SupplyChainPolicyPoints.Validation
                    : SupplyChainPolicyPoints.Admission,
                participant.Id,
                IsAuthoritative: true,
                participant));
}

internal interface ISupplyChainPolicyParticipant : IPolicyParticipant<SupplyChainPolicyContext>
{
    string Id { get; }
}

internal sealed class SignaturePolicyParticipant(
    IPackageSignatureInspectionCapability inspection) : ISupplyChainPolicyParticipant
{
    public string Id => SupplyChainPolicyParticipantIds.Signature;

    public async ValueTask<PolicyDecision> EvaluateAsync(
        SupplyChainPolicyContext context,
        CancellationToken cancellationToken)
    {
        var result = await inspection.InspectSignatureAsync(context.Package, cancellationToken);
        return result.Outcome switch
        {
            SignatureInspectionOutcome.Valid => Allow(result.Detail),
            SignatureInspectionOutcome.Unsigned when !context.RequireSignedPackage =>
                Allow(result.Detail),
            SignatureInspectionOutcome.Unsigned => Deny(
                "supply-chain.signature-required",
                PolicyDecisionEffect.Reject,
                result.Detail),
            _ => Deny(
                "supply-chain.signature-invalid",
                PolicyDecisionEffect.Reject,
                result.Detail)
        };
    }

    private static PolicyDecision Allow(string detail) =>
        new(PolicyDecisionKind.Allow, null, Detail: detail);

    private static PolicyDecision Deny(
        string reason,
        PolicyDecisionEffect effect,
        string detail) =>
        new(PolicyDecisionKind.Deny, reason, effect, detail);
}

internal sealed class ScannerPolicyParticipant(
    IPackageScannerCapability inspection) : ISupplyChainPolicyParticipant
{
    public string Id => SupplyChainPolicyParticipantIds.Scanner;

    public async ValueTask<PolicyDecision> EvaluateAsync(
        SupplyChainPolicyContext context,
        CancellationToken cancellationToken)
    {
        var result = await inspection.ScanAsync(context.Package, cancellationToken);
        return result.Outcome switch
        {
            PackageScannerInspectionOutcome.Clean => new(
                PolicyDecisionKind.Allow,
                null,
                Detail: result.Detail),
            PackageScannerInspectionOutcome.Malicious => new(
                PolicyDecisionKind.Deny,
                "supply-chain.scanner-malicious",
                PolicyDecisionEffect.Reject,
                result.Detail),
            _ => new(
                PolicyDecisionKind.Deny,
                "supply-chain.scanner-inconclusive",
                PolicyDecisionEffect.Quarantine,
                result.Detail)
        };
    }
}

internal sealed class OwnershipPolicyParticipant : ISupplyChainPolicyParticipant
{
    public string Id => SupplyChainPolicyParticipantIds.Ownership;

    public ValueTask<PolicyDecision> EvaluateAsync(
        SupplyChainPolicyContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            context.Administrator || context.IdentityOwnsPackage
                ? new PolicyDecision(PolicyDecisionKind.Allow, null)
                : new PolicyDecision(
                    PolicyDecisionKind.Deny,
                    "supply-chain.ownership-denied",
                    PolicyDecisionEffect.Unauthorized));
    }
}

internal sealed class NamespacePolicyParticipant : ISupplyChainPolicyParticipant
{
    public string Id => SupplyChainPolicyParticipantIds.Namespace;

    public ValueTask<PolicyDecision> EvaluateAsync(
        SupplyChainPolicyContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            context.Administrator || context.NamespaceAllowed
                ? new PolicyDecision(PolicyDecisionKind.Allow, null)
                : new PolicyDecision(
                    PolicyDecisionKind.Deny,
                    "supply-chain.namespace-reserved",
                    PolicyDecisionEffect.Unauthorized));
    }
}

internal sealed class QuotaPolicyParticipant : ISupplyChainPolicyParticipant
{
    public string Id => SupplyChainPolicyParticipantIds.Quota;

    public ValueTask<PolicyDecision> EvaluateAsync(
        SupplyChainPolicyContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            context.QuotaAvailable
                ? new PolicyDecision(PolicyDecisionKind.Allow, null)
                : new PolicyDecision(
                    PolicyDecisionKind.Deny,
                    "supply-chain.quota-exceeded",
                    PolicyDecisionEffect.ResourceLimit));
    }
}
