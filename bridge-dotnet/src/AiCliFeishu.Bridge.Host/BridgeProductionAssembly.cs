using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Adapters.OpenCode;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Host;

internal interface IBridgeActiveOwnerLeaseLifecycle : IAsyncDisposable
{
    bool IsHeld { get; }

    ActiveOwnerLeaseRecord? HeldLease { get; }

    ValueTask<ActiveOwnerLeaseRecord> AcquireAsync(
        CancellationToken cancellationToken = default);

    ValueTask ReleaseAsync(CancellationToken cancellationToken = default);
}

internal interface IBridgePersistentBusinessStateOwner
{
    BridgeBusinessStateSnapshot Snapshot { get; }
}

internal sealed record BridgeApprovalClaim(
    ApprovalState Approval,
    SessionState Session);

internal interface IBridgeActiveApprovalStateOwner
{
    BridgeBusinessStateSnapshot Snapshot { get; }

    ValueTask<BridgeApprovalClaim?> TryClaimApprovalAsync(
        string requestId,
        string sessionId,
        CancellationToken cancellationToken = default);

    ValueTask ReleaseApprovalClaimAsync(
        string requestId,
        CancellationToken cancellationToken = default);

    ValueTask<BridgeApprovalClaim?> ResolveClaimedApprovalAsync(
        string requestId,
        string sessionId,
        string resolution,
        CancellationToken cancellationToken = default);

    ValueTask<BridgeApprovalClaim?> DeferClaimedApprovalAsync(
        string requestId,
        string sessionId,
        CancellationToken cancellationToken = default);
}

internal sealed record BridgeInputClaim(
    InputRequestState Input,
    SessionState Session);

internal sealed record BridgeInputAnswerProgress(
    InputRequestState Input,
    SessionState Session,
    bool Complete);

internal interface IBridgeActiveInputStateOwner
{
    BridgeBusinessStateSnapshot Snapshot { get; }

    ValueTask<BridgeInputAnswerProgress?> TryRecordInputAnswerAsync(
        string requestId,
        string sessionId,
        string questionId,
        IReadOnlyList<string> answers,
        CancellationToken cancellationToken = default);

    ValueTask<BridgeInputClaim?> TryClaimInputAsync(
        string requestId,
        string sessionId,
        CancellationToken cancellationToken = default);

    ValueTask<BridgeInputClaim?> ResolveClaimedInputAsync(
        string requestId,
        string sessionId,
        CancellationToken cancellationToken = default);

    ValueTask<BridgeInputClaim?> DeferClaimedInputAsync(
        string requestId,
        string sessionId,
        CancellationToken cancellationToken = default);

    ValueTask ReleaseInputClaimAsync(
        string requestId,
        CancellationToken cancellationToken = default);

    ValueTask<BridgeInputClaim?> ResetClaimedInputAsync(
        string requestId,
        string sessionId,
        CancellationToken cancellationToken = default);
}

internal interface IBridgeFeishuCredentialSource
{
    BridgeFeishuCredentials Credentials { get; }
}

internal interface IBridgeManagedHookIngress
{
    Task<JsonElement> HandleAsync(
        BridgeManagedIngressKind kind,
        JsonElement payload,
        string traceId,
        CancellationToken cancellationToken = default);
}

internal enum BridgeProductionCapability
{
    ActiveOwnerLease,
    ProductionStoreOwner,
    PersistentBusinessState,
    FeishuCredentials,
    FeishuEventStream,
    FeishuOutboundMessaging,
    ManagedTerminalDirectory,
    ManagedTerminalTransport,
    ManagedRuntimeLifecycle,
    ManagedHookIngress,
    ManagedHookResponses,
    OpenCodeEndpointDirectory,
    OpenCodeEventStream,
    OpenCodeTransport,
    OpenCodeRuntimeLifecycle,
}

internal sealed record BridgeProductionCapabilityOwner(
    BridgeProductionCapability Capability,
    Type OwnerType);

internal sealed class BridgeProductionAssemblyManifest
{
    public BridgeProductionAssemblyManifest(
        IEnumerable<BridgeProductionCapabilityOwner> owners)
    {
        ArgumentNullException.ThrowIfNull(owners);
        Owners = owners.ToArray();
    }

    public IReadOnlyList<BridgeProductionCapabilityOwner> Owners { get; }
}

internal sealed record BridgeProductionAssemblySnapshot(
    string Mode,
    bool Complete,
    IReadOnlyList<BridgeProductionCapability> Capabilities);

/// <summary>
/// Statically audits the ownership-specific DI graph. It never builds a service
/// provider or resolves a service, so a rejected graph cannot acquire a lease,
/// open a socket, access the Store, or start a runtime as a side effect.
/// </summary>
internal static class BridgeProductionAssemblyPreflight
{
    private sealed record CapabilityRequirement(
        BridgeProductionCapability Capability,
        Type ContractType);

    private static readonly CapabilityRequirement[] requirements =
    [
        new(BridgeProductionCapability.ActiveOwnerLease,
            typeof(IBridgeActiveOwnerLeaseLifecycle)),
        new(BridgeProductionCapability.ProductionStoreOwner,
            typeof(IBridgeProductionStoreOwner)),
        new(BridgeProductionCapability.PersistentBusinessState,
            typeof(IBridgePersistentBusinessStateOwner)),
        new(BridgeProductionCapability.FeishuCredentials,
            typeof(IBridgeFeishuCredentialSource)),
        new(BridgeProductionCapability.FeishuEventStream,
            typeof(IFeishuEventSource)),
        new(BridgeProductionCapability.FeishuOutboundMessaging,
            typeof(IFeishuGateway)),
        new(BridgeProductionCapability.ManagedTerminalDirectory,
            typeof(IManagedTerminalDirectory)),
        new(BridgeProductionCapability.ManagedTerminalTransport,
            typeof(IManagedTerminalTransport)),
        new(BridgeProductionCapability.ManagedRuntimeLifecycle,
            typeof(IManagedRuntimeLifecycle)),
        new(BridgeProductionCapability.ManagedHookIngress,
            typeof(IBridgeManagedHookIngress)),
        new(BridgeProductionCapability.ManagedHookResponses,
            typeof(IManagedHookResponseSink)),
        new(BridgeProductionCapability.OpenCodeEndpointDirectory,
            typeof(IOpenCodeEndpointDirectory)),
        new(BridgeProductionCapability.OpenCodeEventStream,
            typeof(IOpenCodeEventSource)),
        new(BridgeProductionCapability.OpenCodeTransport,
            typeof(IOpenCodeTransport)),
        new(BridgeProductionCapability.OpenCodeRuntimeLifecycle,
            typeof(IOpenCodeRuntimeLifecycle)),
    ];

    private static readonly IReadOnlyDictionary<BridgeProductionCapability, Type>
        contractByCapability = requirements.ToDictionary(
            requirement => requirement.Capability,
            requirement => requirement.ContractType);
    private static readonly HashSet<Type> activeOnlyContracts =
    [
        typeof(IBridgeActiveOwnerLeaseLifecycle),
        typeof(IBridgeProductionStoreOwner),
        typeof(IBridgePersistentBusinessStateOwner),
        typeof(IBridgeActiveApprovalStateOwner),
        typeof(IBridgeActiveInputStateOwner),
        typeof(IBridgeFeishuCredentialSource),
        typeof(IBridgeManagedTerminalRegistrationDirectory),
        typeof(IBridgeManagedRuntimeLaunchCoordinator),
        typeof(IBridgeManagedHookIngress),
        typeof(IBridgeOpenCodeEndpointRegistrationDirectory),
        typeof(IBridgeOpenCodeEventStreamOwner),
        typeof(IBridgeOpenCodeRuntimeLifecycleOwner),
    ];
    private static readonly HashSet<Type> activeOnlyImplementationTypes =
    [
        typeof(ActiveOwnerLeaseAcquirer),
        typeof(ActiveOwnerLeaseHostedService),
        typeof(ActiveProductionStoreOwner),
        typeof(ActivePersistentBusinessStateOwner),
        typeof(ActiveFeishuCredentialSource),
        typeof(ActiveFeishuEventSource),
        typeof(ActiveFeishuGateway),
        typeof(ActiveFeishuPromptCoordinator),
        typeof(ActiveFeishuApprovalCoordinator),
        typeof(ActiveFeishuInputCoordinator),
        typeof(ActiveFeishuIntentHandler),
        typeof(ActiveManagedTerminalDirectory),
        typeof(ActiveManagedTerminalTransport),
        typeof(ActiveManagedRuntimeLifecycle),
        typeof(ActiveManagedHookIngress),
        typeof(ActiveManagedHookResponseSink),
        typeof(ActiveOpenCodeEndpointDirectory),
        typeof(ActiveOpenCodeEventSource),
        typeof(ActiveOpenCodeTransport),
        typeof(ActiveOpenCodeRuntimeLifecycle),
    ];

    private static readonly (Type Contract, Type Implementation)[] passivePorts =
    [
        (typeof(IBridgeStoreShadow), typeof(ReadOnlyNodeStoreShadow)),
        (typeof(IFeishuEventSource), typeof(PassiveFeishuEventSource)),
        (typeof(IFeishuGateway), typeof(PassiveFeishuGateway)),
        (typeof(IManagedTerminalDirectory), typeof(PassiveManagedTerminalDirectory)),
        (typeof(IManagedTerminalTransport), typeof(PassiveManagedTerminalTransport)),
        (typeof(IManagedRuntimeLifecycle), typeof(PassiveManagedRuntimeLifecycle)),
        (typeof(IManagedHookResponseSink), typeof(PassiveManagedHookResponseSink)),
        (typeof(IOpenCodeEndpointDirectory), typeof(PassiveOpenCodeEndpointDirectory)),
        (typeof(IOpenCodeEventSource), typeof(PassiveOpenCodeEventSource)),
        (typeof(IOpenCodeTransport), typeof(PassiveOpenCodeTransport)),
        (typeof(IOpenCodeRuntimeLifecycle), typeof(PassiveOpenCodeRuntimeLifecycle)),
    ];

    private static readonly HashSet<Type> passiveImplementationTypes =
        passivePorts.Select(port => port.Implementation)
            .Append(typeof(PassiveOwnerGuardSubsystem))
            .ToHashSet();
    private static readonly Type[] runtimeAdapterTypes =
    [
        typeof(CodexRuntimeAdapter),
        typeof(ClaudeCodeRuntimeAdapter),
        typeof(OpenCodeRuntimeAdapter),
    ];
    private static readonly (Type Contract, Type Implementation)[]
        activeRuntimePorts =
    [
        (typeof(IBridgeRuntimeIngressAssembly),
            typeof(BridgeRuntimeIngressAssembly)),
    ];
    private static readonly Type[] activeRuntimeConcreteServices =
    [
        typeof(BridgeRuntimeEventIngress),
        typeof(ManagedRuntimeHookNormalizer),
        typeof(ManagedRuntimeHookBridge),
        typeof(OpenCodeEventNormalizer),
        typeof(OpenCodeRuntimeEventPump),
        typeof(RuntimeCommandDispatcher),
        typeof(BridgeRuntimeCommandGateway),
        typeof(BridgeRuntimeCommandIngress),
    ];
    private static readonly Type[] activeRuntimeFactoryServices =
    [
        typeof(IBridgeRuntimeEventHandler),
        typeof(IRuntimeEventSink),
        typeof(RuntimeAdapterRegistry),
        typeof(IBridgeRuntimeCommandGateway),
    ];
    private static readonly (Type Contract, Type Implementation)[]
        activeFeishuPorts =
    [
        (typeof(IFeishuCardRenderer), typeof(FeishuCardRenderer)),
        (typeof(IFeishuCardPatchLedger), typeof(InMemoryFeishuCardPatchLedger)),
        (typeof(IFeishuInboundDeduplicator),
            typeof(InMemoryFeishuInboundDeduplicator)),
        (typeof(IBridgeFeishuAdapterAssembly),
            typeof(BridgeFeishuAdapterAssembly)),
    ];
    private static readonly Type[] activeFeishuConcreteServices =
    [
        typeof(ActiveFeishuPromptCoordinator),
        typeof(ActiveFeishuApprovalCoordinator),
        typeof(ActiveFeishuInputCoordinator),
        typeof(ActiveFeishuIntentHandler),
        typeof(BridgeFeishuIntentIngress),
        typeof(FeishuEventNormalizer),
        typeof(FeishuInteractionCoordinator),
        typeof(FeishuEventPump),
        typeof(BridgeBoundaryCatalog),
        typeof(BridgeBoundarySubsystem),
        typeof(BridgeFeishuEventSubsystem),
    ];
    private static readonly Type[] activeFeishuFactoryServices =
    [
        typeof(IBridgeActiveApprovalStateOwner),
        typeof(IBridgeActiveInputStateOwner),
        typeof(IBridgeFeishuIntentHandler),
        typeof(IFeishuIntentSink),
    ];

    public static BridgeProductionAssemblySnapshot Validate(
        BridgeHostOptions options,
        IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);
        return options.OwnershipMode switch
        {
            BridgeOwnershipMode.Passive => ValidatePassive(services),
            BridgeOwnershipMode.Active => ValidateActive(services),
            _ => throw new InvalidOperationException(
                $"未知的 Bridge Host 所有权模式 {options.OwnershipMode}。"),
        };
    }

    private static BridgeProductionAssemblySnapshot ValidatePassive(
        IServiceCollection services)
    {
        if (services.Any(descriptor =>
            descriptor.ServiceType == typeof(BridgeProductionAssemblyManifest) ||
            activeOnlyContracts.Contains(descriptor.ServiceType) ||
            activeOnlyImplementationTypes.Contains(descriptor.ServiceType) ||
            descriptor.ImplementationType is not null &&
            activeOnlyImplementationTypes.Contains(descriptor.ImplementationType)))
        {
            throw new InvalidOperationException(
                "Passive Host 不得声明或注册 Active 专用生产能力。");
        }

        foreach (var (contract, implementation) in passivePorts)
        {
            var registrations = services
                .Where(descriptor => descriptor.ServiceType == contract)
                .ToArray();
            if (registrations.Length != 1 ||
                registrations[0].ImplementationType != implementation)
            {
                throw new InvalidOperationException(
                    $"Passive Host 端口 {contract.Name} 必须且只能使用 {implementation.Name}。");
            }
        }

        var guards = services
            .Where(descriptor =>
                descriptor.ServiceType == typeof(IBridgeHostSubsystem) &&
                descriptor.ImplementationType == typeof(PassiveOwnerGuardSubsystem))
            .ToArray();
        if (guards.Length != 1)
        {
            throw new InvalidOperationException(
                "Passive Host 必须且只能注册一个只读生产所有权守卫。");
        }
        ValidateHostedLifecycle(
            services,
            "Passive Host",
            typeof(BridgeInstanceLeaseService),
            typeof(BridgeRuntimeWorker));
        return new("passive", true, []);
    }

    private static BridgeProductionAssemblySnapshot ValidateActive(
        IServiceCollection services)
    {
        var passiveRegistration = services.FirstOrDefault(descriptor =>
            passiveImplementationTypes.Contains(descriptor.ServiceType) ||
            descriptor.ImplementationType is not null &&
            passiveImplementationTypes.Contains(descriptor.ImplementationType));
        if (passiveRegistration is not null)
        {
            var implementation = passiveRegistration.ImplementationType ??
                passiveRegistration.ServiceType;
            throw new InvalidOperationException(
                $"Active Host 不得回退到 Passive 组件 {implementation.Name}。");
        }

        var manifests = services
            .Where(descriptor =>
                descriptor.ServiceType == typeof(BridgeProductionAssemblyManifest))
            .ToArray();
        if (manifests.Length != 1 ||
            manifests[0].ImplementationInstance is not BridgeProductionAssemblyManifest manifest)
        {
            throw new InvalidOperationException(
                "Active Host 必须且只能提供一个静态生产装配清单。");
        }

        var invalidCapabilities = manifest.Owners
            .Where(owner => !contractByCapability.ContainsKey(owner.Capability))
            .Select(owner => owner.Capability.ToString())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (invalidCapabilities.Length != 0)
        {
            throw new InvalidOperationException(
                $"Active Host 声明了未知生产能力：{string.Join(", ", invalidCapabilities)}。");
        }

        var duplicateCapabilities = manifest.Owners
            .GroupBy(owner => owner.Capability)
            .Where(group => group.Count() != 1)
            .Select(group => group.Key)
            .OrderBy(capability => capability)
            .ToArray();
        if (duplicateCapabilities.Length != 0)
        {
            throw new InvalidOperationException(
                $"Active Host 生产能力所有者不唯一：{Names(duplicateCapabilities)}。");
        }

        var declared = manifest.Owners
            .Select(owner => owner.Capability)
            .ToHashSet();
        var missing = requirements
            .Select(requirement => requirement.Capability)
            .Where(capability => !declared.Contains(capability))
            .ToArray();
        if (missing.Length != 0)
        {
            throw new InvalidOperationException(
                $"Active Host 生产装配不完整，缺少能力：{Names(missing)}。");
        }

        ValidateHostedLifecycle(
            services,
            "Active Host",
            typeof(BridgeInstanceLeaseService),
            typeof(ActiveOwnerLeaseHostedService),
            typeof(BridgeRuntimeWorker));
        ValidateActiveRuntimeAssembly(services);
        ValidateActiveFeishuAssembly(services);

        foreach (var owner in manifest.Owners)
        {
            var contractType = contractByCapability[owner.Capability];
            if (owner.OwnerType is null ||
                owner.OwnerType.IsAbstract ||
                owner.OwnerType.IsInterface ||
                !contractType.IsAssignableFrom(owner.OwnerType) ||
                passiveImplementationTypes.Contains(owner.OwnerType))
            {
                throw new InvalidOperationException(
                    $"Active Host 生产能力 {owner.Capability} 没有有效的生产实现所有者。");
            }
            var registrations = services
                .Where(descriptor => descriptor.ServiceType == contractType)
                .ToArray();
            if (registrations.Length != 1 ||
                registrations[0].ImplementationType != owner.OwnerType)
            {
                throw new InvalidOperationException(
                    $"Active Host 生产能力 {owner.Capability} 的实现所有者未唯一注册。");
            }
        }

        return new(
            "active",
            true,
            requirements.Select(requirement => requirement.Capability).ToArray());
    }

    private static void ValidateHostedLifecycle(
        IServiceCollection services,
        string hostName,
        params Type[] expectedImplementations)
    {
        var registrations = services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .ToArray();
        if (registrations.Length != expectedImplementations.Length ||
            registrations.Where((descriptor, index) =>
                descriptor.ImplementationType != expectedImplementations[index]).Any())
        {
            throw new InvalidOperationException(
                $"{hostName} 后台生命周期注册缺失、重复、越序或包含未知实现。");
        }
    }

    private static void ValidateActiveRuntimeAssembly(IServiceCollection services)
    {
        foreach (var (contract, implementation) in activeRuntimePorts)
        {
            RequireSingleImplementation(
                services,
                contract,
                implementation,
                "Active Host 标准 Runtime 装配");
        }
        foreach (var implementation in activeRuntimeConcreteServices)
        {
            RequireSingleImplementation(
                services,
                implementation,
                implementation,
                "Active Host 标准 Runtime 装配");
        }

        var adapters = services
            .Where(descriptor => descriptor.ServiceType == typeof(IRuntimeAdapter))
            .ToArray();
        if (adapters.Length != runtimeAdapterTypes.Length ||
            adapters.Where((descriptor, index) =>
                descriptor.ImplementationType != runtimeAdapterTypes[index]).Any())
        {
            throw new InvalidOperationException(
                "Active Host 必须按 Codex、Claude Code、OpenCode 顺序唯一注册标准 Runtime Adapter。");
        }

        foreach (var serviceType in activeRuntimeFactoryServices)
        {
            var registrations = services
                .Where(descriptor => descriptor.ServiceType == serviceType)
                .ToArray();
            if (registrations.Length != 1 ||
                registrations[0].ImplementationFactory is null)
            {
                throw new InvalidOperationException(
                    $"Active Host 标准 Runtime 装配端口 {serviceType.Name} 必须且只能使用组合根工厂。");
            }
        }
    }

    private static void ValidateActiveFeishuAssembly(IServiceCollection services)
    {
        foreach (var (contract, implementation) in activeFeishuPorts)
        {
            RequireSingleImplementation(
                services,
                contract,
                implementation,
                "Active Host 标准飞书装配");
        }
        foreach (var implementation in activeFeishuConcreteServices)
        {
            RequireSingleImplementation(
                services,
                implementation,
                implementation,
                "Active Host 标准飞书装配");
        }
        foreach (var serviceType in activeFeishuFactoryServices)
        {
            var registrations = services
                .Where(descriptor => descriptor.ServiceType == serviceType)
                .ToArray();
            if (registrations.Length != 1 ||
                registrations[0].ImplementationFactory is null)
            {
                throw new InvalidOperationException(
                    $"Active Host 标准飞书装配端口 {serviceType.Name} 必须且只能使用组合根工厂。");
            }
        }
    }

    private static void RequireSingleImplementation(
        IServiceCollection services,
        Type contract,
        Type implementation,
        string owner)
    {
        var registrations = services
            .Where(descriptor => descriptor.ServiceType == contract)
            .ToArray();
        if (registrations.Length != 1 ||
            registrations[0].ImplementationType != implementation)
        {
            throw new InvalidOperationException(
                $"{owner}端口 {contract.Name} 必须且只能使用 {implementation.Name}。");
        }
    }

    private static string Names(IEnumerable<BridgeProductionCapability> capabilities) =>
        string.Join(", ", capabilities
            .OrderBy(capability => capability)
            .Select(capability => capability.ToString()));
}
