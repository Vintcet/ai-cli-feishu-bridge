using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal sealed record BridgeManagedTerminalRegistration(
    string TerminalId,
    string TerminalSecret,
    string Cwd,
    string Runtime,
    bool Elevated,
    bool Ready);

internal sealed record BridgeManagedTerminalClaim(
    string TerminalId,
    string Runtime,
    bool Elevated,
    DateTimeOffset CreatedAt,
    long Generation,
    bool ExistingClaim);

internal sealed record BridgeManagedTerminalIdentity(
    string TerminalId,
    string SessionExternalId,
    string Cwd,
    string Runtime,
    bool Elevated);

internal sealed record BridgeManagedTerminalDirectorySnapshot(
    bool Initialized,
    int Registrations,
    int Online,
    int Ready,
    int Claimed);

internal sealed record BridgeManagedTerminalRegistrationStatus(
    string TerminalId,
    bool Online,
    bool Ready,
    string? SessionExternalId,
    DateTimeOffset LastSeenAt);

internal interface IBridgeManagedTerminalRegistrationDirectory
{
    BridgeManagedTerminalDirectorySnapshot Snapshot { get; }

    void Register(BridgeManagedTerminalRegistration registration);

    bool Unregister(string terminalId);

    BridgeManagedTerminalClaim? Claim(
        string cwd,
        string runtime,
        string sessionExternalId);

    BridgeManagedTerminalClaim? ClaimById(
        string terminalId,
        string cwd,
        string runtime,
        string sessionExternalId,
        bool? elevated = null);

    BridgeManagedTerminalIdentity? FindClaimBySession(string sessionExternalId);

    BridgeManagedTerminalIdentity? FindClaimByTerminal(string terminalId);

    BridgeManagedTerminalRegistrationStatus? GetStatus(string terminalId);

    void Release(string sessionExternalId);

    bool IsCurrent(ManagedTerminalTarget target);

    bool IsAuthenticated(string terminalId, string terminalSecret);
}

internal sealed class ActiveManagedTerminalDirectory(
    BridgeHostOptions options,
    IBridgeProductionStoreOwner storeOwner,
    TimeProvider? timeProvider = null)
    : IManagedTerminalDirectory,
      IBridgeManagedTerminalRegistrationDirectory,
      IBridgeHostSubsystem,
      IBridgeHostSubsystemHealth
{
    private static readonly TimeSpan OnlineLifetime = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RetentionLifetime = TimeSpan.FromSeconds(60);
    private readonly object sync = new();
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, Registration> registrations =
        new(StringComparer.Ordinal);
    private Dictionary<string, PersistedLink> persistedLinks =
        new(StringComparer.Ordinal);
    private bool initialized;
    private long nextGeneration;

    public string Name => "managed-terminal-directory";

    public BridgeManagedTerminalDirectorySnapshot Snapshot
    {
        get
        {
            lock (sync)
            {
                var now = clock.GetUtcNow();
                Prune(now);
                return SnapshotLocked(now);
            }
        }
    }

    public BridgeComponentHealth ComponentHealth
    {
        get
        {
            var current = Snapshot;
            return current.Initialized
                ? new(
                    Name,
                    "ready",
                    $"registrations={current.Registrations} " +
                    $"online={current.Online} ready={current.Ready} " +
                    $"claimed={current.Claimed}")
                : new(Name, "failed", "not-initialized");
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureActive();
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            lock (sync)
            {
                if (initialized)
                {
                    return;
                }
            }
            var store = await storeOwner.ReadAsync(cancellationToken);
            var links = BuildPersistedLinks(store.Sessions);
            lock (sync)
            {
                persistedLinks = links;
                registrations.Clear();
                initialized = true;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        await lifecycleGate.WaitAsync(CancellationToken.None);
        try
        {
            lock (sync)
            {
                registrations.Clear();
                persistedLinks.Clear();
                initialized = false;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public ManagedTerminalTarget? FindBySession(string sessionExternalId)
    {
        if (!ValidSessionId(sessionExternalId))
        {
            return null;
        }
        lock (sync)
        {
            EnsureInitializedLocked();
            var now = clock.GetUtcNow();
            Prune(now);
            var registration = registrations.Values.SingleOrDefault(item =>
                string.Equals(
                    item.SessionExternalId,
                    sessionExternalId,
                    StringComparison.Ordinal));
            if (registration is null || !IsOnline(registration, now))
            {
                return null;
            }
            return Target(registration);
        }
    }

    public BridgeManagedTerminalIdentity? FindClaimBySession(string sessionExternalId)
    {
        sessionExternalId = RequireSessionId(sessionExternalId);
        lock (sync)
        {
            EnsureInitializedLocked();
            var registration = registrations.Values.SingleOrDefault(item =>
                string.Equals(
                    item.SessionExternalId,
                    sessionExternalId,
                    StringComparison.Ordinal));
            if (registration is not null)
            {
                return Identity(registration, sessionExternalId);
            }
            var link = persistedLinks.Values.SingleOrDefault(item =>
                string.Equals(
                    item.SessionExternalId,
                    sessionExternalId,
                    StringComparison.Ordinal));
            return link is null ? null : Identity(link);
        }
    }

    public BridgeManagedTerminalIdentity? FindClaimByTerminal(string terminalId)
    {
        terminalId = RequireTerminalId(terminalId);
        lock (sync)
        {
            EnsureInitializedLocked();
            if (registrations.TryGetValue(terminalId, out var registration) &&
                registration.SessionExternalId is { } sessionExternalId)
            {
                return Identity(registration, sessionExternalId);
            }
            return persistedLinks.TryGetValue(terminalId, out var link)
                ? Identity(link)
                : null;
        }
    }

    public BridgeManagedTerminalRegistrationStatus? GetStatus(string terminalId)
    {
        terminalId = RequireTerminalId(terminalId);
        lock (sync)
        {
            EnsureInitializedLocked();
            var now = clock.GetUtcNow();
            Prune(now);
            if (!registrations.TryGetValue(terminalId, out var registration))
            {
                return null;
            }
            return new(
                registration.TerminalId,
                IsOnline(registration, now),
                registration.Ready,
                registration.SessionExternalId,
                registration.LastSeenAt);
        }
    }

    public void Register(BridgeManagedTerminalRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var terminalId = RequireTerminalId(registration.TerminalId);
        var terminalSecret = RequireTerminalSecret(registration.TerminalSecret);
        var cwd = NormalizeCwd(registration.Cwd);
        var runtime = RequireRuntime(registration.Runtime);
        lock (sync)
        {
            EnsureInitializedLocked();
            var now = clock.GetUtcNow();
            Prune(now);
            if (registrations.TryGetValue(terminalId, out var current))
            {
                if (!string.Equals(current.NormalizedCwd, cwd, CwdComparison) ||
                    !string.Equals(current.Runtime, runtime, StringComparison.Ordinal) ||
                    current.Elevated != registration.Elevated ||
                    !SecretEquals(current.TerminalSecret, terminalSecret))
                {
                    throw new InvalidOperationException(
                        "托管终端心跳与已登记身份不一致。");
                }
                current.Ready = registration.Ready;
                current.LastSeenAt = Later(current.LastSeenAt, now);
                return;
            }

            persistedLinks.TryGetValue(terminalId, out var link);
            if (link is not null)
            {
                if (!string.Equals(link.NormalizedCwd, cwd, CwdComparison) ||
                    link.Runtime is not null &&
                    !string.Equals(link.Runtime, runtime, StringComparison.Ordinal) ||
                    link.Elevated != registration.Elevated)
                {
                    throw new InvalidOperationException(
                        "托管终端心跳与持久化会话身份不一致。");
                }
                EnsureSessionNotClaimed(link.SessionExternalId, terminalId);
            }
            registrations.Add(
                terminalId,
                new Registration(
                    terminalId,
                    terminalSecret,
                    cwd,
                    runtime,
                    registration.Elevated,
                    registration.Ready,
                    now,
                    now,
                    link?.SessionExternalId,
                    ++nextGeneration));
        }
    }

    public bool Unregister(string terminalId)
    {
        terminalId = RequireTerminalId(terminalId);
        lock (sync)
        {
            EnsureInitializedLocked();
            persistedLinks.Remove(terminalId);
            return registrations.Remove(terminalId);
        }
    }

    public BridgeManagedTerminalClaim? Claim(
        string cwd,
        string runtime,
        string sessionExternalId)
    {
        var normalizedCwd = NormalizeCwd(cwd);
        runtime = RequireRuntime(runtime);
        sessionExternalId = RequireSessionId(sessionExternalId);
        lock (sync)
        {
            EnsureInitializedLocked();
            var now = clock.GetUtcNow();
            Prune(now);
            var candidate = registrations.Values
                .Where(item =>
                    IsOnline(item, now) &&
                    string.Equals(item.NormalizedCwd, normalizedCwd, CwdComparison) &&
                    string.Equals(item.Runtime, runtime, StringComparison.Ordinal) &&
                    (item.SessionExternalId is null ||
                     string.Equals(
                         item.SessionExternalId,
                         sessionExternalId,
                         StringComparison.Ordinal)))
                .OrderBy(item => item.CreatedAt)
                .ThenBy(item => item.Generation)
                .FirstOrDefault();
            return candidate is null
                ? null
                : ClaimLocked(candidate, sessionExternalId);
        }
    }

    public BridgeManagedTerminalClaim? ClaimById(
        string terminalId,
        string cwd,
        string runtime,
        string sessionExternalId,
        bool? elevated = null)
    {
        terminalId = RequireTerminalId(terminalId);
        var normalizedCwd = NormalizeCwd(cwd);
        runtime = RequireRuntime(runtime);
        sessionExternalId = RequireSessionId(sessionExternalId);
        lock (sync)
        {
            EnsureInitializedLocked();
            var now = clock.GetUtcNow();
            Prune(now);
            if (!registrations.TryGetValue(terminalId, out var registration) ||
                !IsOnline(registration, now))
            {
                return null;
            }
            if (!string.Equals(
                    registration.NormalizedCwd,
                    normalizedCwd,
                    CwdComparison) ||
                !string.Equals(registration.Runtime, runtime, StringComparison.Ordinal) ||
                elevated is not null && registration.Elevated != elevated)
            {
                throw new InvalidOperationException(
                    "托管终端 ID 与项目目录或运行时不匹配。");
            }
            return ClaimLocked(registration, sessionExternalId);
        }
    }

    public void Release(string sessionExternalId)
    {
        sessionExternalId = RequireSessionId(sessionExternalId);
        lock (sync)
        {
            EnsureInitializedLocked();
            foreach (var registration in registrations.Values.Where(item =>
                         string.Equals(
                             item.SessionExternalId,
                             sessionExternalId,
                             StringComparison.Ordinal)))
            {
                registration.SessionExternalId = null;
            }
            foreach (var terminalId in persistedLinks
                         .Where(item => string.Equals(
                             item.Value.SessionExternalId,
                             sessionExternalId,
                             StringComparison.Ordinal))
                         .Select(item => item.Key)
                         .ToArray())
            {
                persistedLinks.Remove(terminalId);
            }
        }
    }

    public bool IsCurrent(ManagedTerminalTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (sync)
        {
            EnsureInitializedLocked();
            var now = clock.GetUtcNow();
            Prune(now);
            return registrations.TryGetValue(target.TerminalId, out var registration) &&
                registration.Generation == target.Generation &&
                SecretEquals(registration.TerminalSecret, target.TerminalSecret) &&
                registration.Ready &&
                IsOnline(registration, now) &&
                string.Equals(
                    registration.SessionExternalId,
                    target.SessionExternalId,
                    StringComparison.Ordinal);
        }
    }

    public bool IsAuthenticated(string terminalId, string terminalSecret)
    {
        if (!ValidTerminalId(terminalId) || !ValidTerminalSecret(terminalSecret))
        {
            return false;
        }
        lock (sync)
        {
            EnsureInitializedLocked();
            var now = clock.GetUtcNow();
            Prune(now);
            return registrations.TryGetValue(terminalId, out var registration) &&
                IsOnline(registration, now) &&
                SecretEquals(registration.TerminalSecret, terminalSecret);
        }
    }

    private BridgeManagedTerminalClaim ClaimLocked(
        Registration registration,
        string sessionExternalId)
    {
        var existingClaim = registration.SessionExternalId is not null;
        if (registration.SessionExternalId is not null &&
            !string.Equals(
                registration.SessionExternalId,
                sessionExternalId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "托管终端已经属于另一个会话。");
        }
        EnsureSessionNotClaimed(sessionExternalId, registration.TerminalId);
        registration.SessionExternalId = sessionExternalId;
        registration.Ready = true;
        persistedLinks[registration.TerminalId] = new(
            registration.TerminalId,
            sessionExternalId,
            registration.NormalizedCwd,
            registration.Runtime,
            registration.Elevated);
        return new(
            registration.TerminalId,
            registration.Runtime,
            registration.Elevated,
            registration.CreatedAt,
            registration.Generation,
            existingClaim);
    }

    private void EnsureSessionNotClaimed(string sessionExternalId, string terminalId)
    {
        if (registrations.Values.Any(item =>
            !string.Equals(item.TerminalId, terminalId, StringComparison.Ordinal) &&
            string.Equals(
                item.SessionExternalId,
                sessionExternalId,
                StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "目标会话已经属于另一个托管终端。");
        }
    }

    private static Dictionary<string, PersistedLink> BuildPersistedLinks(
        SessionStoreDocument document)
    {
        var links = new Dictionary<string, PersistedLink>(StringComparer.Ordinal);
        var linkedSessions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in document.Sessions.Values)
        {
            if (string.Equals(session.Status, "ended", StringComparison.Ordinal) ||
                string.Equals(
                    ExtensionString(session, "source", strict: false),
                    "managed_window",
                    StringComparison.Ordinal))
            {
                continue;
            }
            var terminalId = ExtensionString(
                session,
                "managedTerminalId",
                strict: true);
            if (terminalId is null)
            {
                continue;
            }
            if (!ValidTerminalId(terminalId) ||
                !ValidSessionId(session.SessionId))
            {
                throw new InvalidDataException(
                    "生产 Store 包含无效的托管终端绑定。");
            }
            var runtime = session.Runtime;
            if (runtime is not null &&
                runtime is not RuntimeNames.Codex and not RuntimeNames.ClaudeCode)
            {
                throw new InvalidDataException(
                    "生产 Store 的托管终端绑定包含无效运行时。");
            }
            string normalizedCwd;
            try
            {
                normalizedCwd = NormalizeCwd(session.Cwd);
            }
            catch (Exception error) when (
                error is ArgumentException or IOException or NotSupportedException)
            {
                throw new InvalidDataException(
                    "生产 Store 的托管终端绑定包含无效工作目录。",
                    error);
            }
            if (!links.TryAdd(
                terminalId,
                new(
                    terminalId,
                    session.SessionId,
                    normalizedCwd,
                    runtime,
                    ExtensionBoolean(
                        session,
                        "managedTerminalElevated",
                        strict: true) ?? false)) ||
                !linkedSessions.Add(session.SessionId))
            {
                throw new InvalidDataException(
                    "生产 Store 包含冲突的托管终端绑定。");
            }
        }
        return links;
    }

    private BridgeManagedTerminalDirectorySnapshot SnapshotLocked(DateTimeOffset now) =>
        new(
            initialized,
            registrations.Count,
            registrations.Values.Count(item => IsOnline(item, now)),
            registrations.Values.Count(item => item.Ready && IsOnline(item, now)),
            registrations.Values.Count(item => item.SessionExternalId is not null));

    private static ManagedTerminalTarget Target(Registration registration) => new(
        registration.TerminalId,
        registration.SessionExternalId!,
        registration.Ready,
        registration.Generation,
        registration.TerminalSecret);

    private static BridgeManagedTerminalIdentity Identity(
        Registration registration,
        string sessionExternalId) => new(
            registration.TerminalId,
            sessionExternalId,
            registration.NormalizedCwd,
            registration.Runtime,
            registration.Elevated);

    private static BridgeManagedTerminalIdentity Identity(PersistedLink link) => new(
        link.TerminalId,
        link.SessionExternalId,
        link.NormalizedCwd,
        link.Runtime ?? RuntimeNames.Codex,
        link.Elevated);

    private void Prune(DateTimeOffset now)
    {
        foreach (var terminalId in registrations
                     .Where(item => Age(now, item.Value.LastSeenAt) > RetentionLifetime)
                     .Select(item => item.Key)
                     .ToArray())
        {
            registrations.Remove(terminalId);
        }
    }

    private static bool IsOnline(Registration registration, DateTimeOffset now) =>
        Age(now, registration.LastSeenAt) <= OnlineLifetime;

    private static TimeSpan Age(DateTimeOffset now, DateTimeOffset observedAt) =>
        now <= observedAt ? TimeSpan.Zero : now - observedAt;

    private static DateTimeOffset Later(DateTimeOffset left, DateTimeOffset right) =>
        left >= right ? left : right;

    private void EnsureActive()
    {
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            throw new InvalidOperationException(
                "托管终端生产目录只能用于 Active Host。");
        }
    }

    private void EnsureInitializedLocked()
    {
        if (!initialized)
        {
            throw new InvalidOperationException("托管终端生产目录尚未初始化。");
        }
    }

    private static string RequireTerminalId(string terminalId) =>
        ValidTerminalId(terminalId)
            ? terminalId
            : throw new ArgumentException("托管终端 ID 无效。", nameof(terminalId));

    private static bool ValidTerminalId(string terminalId) =>
        !string.IsNullOrEmpty(terminalId) &&
        terminalId.Length is >= 8 and <= 64 &&
        terminalId.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static string RequireTerminalSecret(string terminalSecret) =>
        ValidTerminalSecret(terminalSecret)
            ? terminalSecret
            : throw new ArgumentException(
                "托管终端密钥无效。",
                nameof(terminalSecret));

    private static bool ValidTerminalSecret(string terminalSecret) =>
        terminalSecret?.Length == 64 && terminalSecret.All(Uri.IsHexDigit);

    private static bool SecretEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
            CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string RequireSessionId(string sessionExternalId) =>
        ValidSessionId(sessionExternalId)
            ? sessionExternalId
            : throw new ArgumentException(
                "托管终端会话 ID 无效。",
                nameof(sessionExternalId));

    private static bool ValidSessionId(string sessionExternalId) =>
        !string.IsNullOrWhiteSpace(sessionExternalId) &&
        sessionExternalId.Length <= 256 &&
        !sessionExternalId.Any(char.IsControl);

    private static string RequireRuntime(string runtime) => runtime switch
    {
        RuntimeNames.Codex => RuntimeNames.Codex,
        RuntimeNames.ClaudeCode => RuntimeNames.ClaudeCode,
        _ => throw new ArgumentException("托管终端运行时无效。", nameof(runtime)),
    };

    private static string NormalizeCwd(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !Path.IsPathFullyQualified(cwd.Trim()))
        {
            throw new ArgumentException("托管终端工作目录无效。", nameof(cwd));
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd.Trim()));
    }

    private static string? ExtensionString(
        SessionStoreRecord session,
        string name,
        bool strict)
    {
        if (session.ExtensionData is null)
        {
            return null;
        }
        var values = session.ExtensionData
            .Where(item => string.Equals(
                item.Key,
                name,
                StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Value)
            .Take(2)
            .ToArray();
        if (values.Length == 0 ||
            values[0].ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (values.Length != 1 || values[0].ValueKind is not JsonValueKind.String)
        {
            return strict
                ? throw new InvalidDataException(
                    $"生产 Store 的托管终端绑定包含无效扩展字段 {name}。")
                : null;
        }
        var value = values[0].GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool? ExtensionBoolean(
        SessionStoreRecord session,
        string name,
        bool strict)
    {
        if (session.ExtensionData is null)
        {
            return null;
        }
        var values = session.ExtensionData
            .Where(item => string.Equals(
                item.Key,
                name,
                StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Value)
            .Take(2)
            .ToArray();
        if (values.Length == 0 ||
            values[0].ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (values.Length != 1 ||
            values[0].ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            return strict
                ? throw new InvalidDataException(
                    $"生产 Store 的托管终端绑定包含无效扩展字段 {name}。")
                : null;
        }
        return values[0].GetBoolean();
    }

    private static StringComparison CwdComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed class Registration(
        string terminalId,
        string terminalSecret,
        string normalizedCwd,
        string runtime,
        bool elevated,
        bool ready,
        DateTimeOffset createdAt,
        DateTimeOffset lastSeenAt,
        string? sessionExternalId,
        long generation)
    {
        public string TerminalId { get; } = terminalId;
        public string TerminalSecret { get; } = terminalSecret;
        public string NormalizedCwd { get; } = normalizedCwd;
        public string Runtime { get; } = runtime;
        public bool Elevated { get; } = elevated;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public long Generation { get; } = generation;
        public bool Ready { get; set; } = ready;
        public DateTimeOffset LastSeenAt { get; set; } = lastSeenAt;
        public string? SessionExternalId { get; set; } = sessionExternalId;
    }

    private sealed record PersistedLink(
        string TerminalId,
        string SessionExternalId,
        string NormalizedCwd,
        string? Runtime,
        bool Elevated);
}
