using AiCliFeishu.Bridge.Adapters.OpenCode;

namespace AiCliFeishu.Bridge.Host;

internal sealed record BridgeOpenCodeEndpointIdentity(
    int Port,
    string Cwd,
    long Generation,
    bool Ready)
{
    public OpenCodeEndpoint Endpoint => new(
        new Uri($"http://127.0.0.1:{Port}/", UriKind.Absolute),
        Cwd,
        Ready);
}

internal sealed record BridgeOpenCodeEndpointDirectorySnapshot(
    bool Initialized,
    long Revision,
    int Registrations,
    int Ready,
    int Sessions);

internal interface IBridgeOpenCodeEndpointRegistrationDirectory
{
    BridgeOpenCodeEndpointDirectorySnapshot Snapshot { get; }

    BridgeOpenCodeEndpointIdentity Register(int port, string cwd);

    bool Unregister(int port);

    bool Unregister(int port, long generation);

    bool SetReady(int port, long generation, bool ready);

    bool RememberSession(int port, long generation, string sessionExternalId);

    bool ForgetSession(int port, long generation, string sessionExternalId);

    IReadOnlyList<BridgeOpenCodeEndpointIdentity> ListRegistrations();

    ValueTask<long> WaitForChangeAsync(
        long observedRevision,
        CancellationToken cancellationToken = default);
}

internal sealed class ActiveOpenCodeEndpointDirectory :
    IOpenCodeEndpointDirectory,
    IBridgeOpenCodeEndpointRegistrationDirectory,
    IBridgeHostSubsystem,
    IBridgeHostSubsystemHealth
{
    private const int DefaultRegistrationCapacity = 1_024;
    private const int DefaultSessionCapacity = 8_192;
    private readonly object sync = new();
    private readonly BridgeHostOptions options;
    private readonly int registrationCapacity;
    private readonly int sessionCapacity;
    private readonly Dictionary<int, Registration> registrations = [];
    private readonly Dictionary<string, SessionMapping> sessions =
        new(StringComparer.Ordinal);
    private TaskCompletionSource<long> changed = NewChangeSource();
    private bool initialized;
    private long revision;
    private long nextGeneration;
    private long nextSessionSequence;

    public ActiveOpenCodeEndpointDirectory(BridgeHostOptions options)
        : this(options, DefaultRegistrationCapacity, DefaultSessionCapacity)
    {
    }

    internal ActiveOpenCodeEndpointDirectory(
        BridgeHostOptions options,
        int registrationCapacity,
        int sessionCapacity)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.registrationCapacity = registrationCapacity > 0
            ? registrationCapacity
            : throw new ArgumentOutOfRangeException(nameof(registrationCapacity));
        this.sessionCapacity = sessionCapacity > 0
            ? sessionCapacity
            : throw new ArgumentOutOfRangeException(nameof(sessionCapacity));
    }

    public string Name => "opencode-endpoint-directory";

    public BridgeOpenCodeEndpointDirectorySnapshot Snapshot
    {
        get
        {
            lock (sync)
            {
                return SnapshotLocked();
            }
        }
    }

    public BridgeComponentHealth ComponentHealth
    {
        get
        {
            var snapshot = Snapshot;
            return snapshot.Initialized
                ? new(
                    Name,
                    "healthy",
                    $"registered={snapshot.Registrations};ready={snapshot.Ready};sessions={snapshot.Sessions}")
                : new(Name, "starting");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureActive();
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (initialized)
            {
                return Task.CompletedTask;
            }
            initialized = true;
            SignalChangedLocked();
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        lock (sync)
        {
            registrations.Clear();
            sessions.Clear();
            initialized = false;
            SignalChangedLocked();
        }
        return Task.CompletedTask;
    }

    public OpenCodeEndpoint? FindBySession(string sessionExternalId)
    {
        if (!ValidSessionId(sessionExternalId))
        {
            return null;
        }
        lock (sync)
        {
            EnsureInitializedLocked();
            if (!sessions.TryGetValue(sessionExternalId, out var mapping) ||
                !registrations.TryGetValue(mapping.Port, out var registration) ||
                registration.Generation != mapping.Generation)
            {
                return null;
            }
            return Identity(registration).Endpoint;
        }
    }

    public IReadOnlyList<OpenCodeEndpoint> ListReady()
    {
        lock (sync)
        {
            EnsureInitializedLocked();
            return registrations.Values
                .Where(registration => registration.Ready)
                .OrderBy(registration => registration.Port)
                .Select(registration => Identity(registration).Endpoint)
                .ToArray();
        }
    }

    public IReadOnlyList<BridgeOpenCodeEndpointIdentity> ListRegistrations()
    {
        lock (sync)
        {
            EnsureInitializedLocked();
            return registrations.Values
                .OrderBy(registration => registration.Port)
                .Select(Identity)
                .ToArray();
        }
    }

    public BridgeOpenCodeEndpointIdentity Register(int port, string cwd)
    {
        EnsureActive();
        port = RequirePort(port);
        cwd = NormalizeCwd(cwd);
        lock (sync)
        {
            EnsureInitializedLocked();
            if (!registrations.ContainsKey(port) &&
                registrations.Count >= registrationCapacity)
            {
                throw new InvalidOperationException("OpenCode 端点目录已达到容量上限。");
            }
            RemoveSessionsLocked(port);
            var generation = checked(++nextGeneration);
            var registration = new Registration(port, cwd, generation, Ready: false);
            registrations[port] = registration;
            SignalChangedLocked();
            return Identity(registration);
        }
    }

    public bool Unregister(int port)
    {
        EnsureActive();
        port = RequirePort(port);
        lock (sync)
        {
            EnsureInitializedLocked();
            return registrations.TryGetValue(port, out var registration) &&
                RemoveRegistrationLocked(port, registration.Generation);
        }
    }

    public bool Unregister(int port, long generation)
    {
        EnsureActive();
        port = RequirePort(port);
        if (generation <= 0)
        {
            return false;
        }
        lock (sync)
        {
            EnsureInitializedLocked();
            return RemoveRegistrationLocked(port, generation);
        }
    }

    public bool SetReady(int port, long generation, bool ready)
    {
        EnsureActive();
        port = RequirePort(port);
        lock (sync)
        {
            EnsureInitializedLocked();
            if (!registrations.TryGetValue(port, out var registration) ||
                registration.Generation != generation)
            {
                return false;
            }
            if (registration.Ready == ready)
            {
                return true;
            }
            registrations[port] = registration with { Ready = ready };
            SignalChangedLocked();
            return true;
        }
    }

    public bool RememberSession(
        int port,
        long generation,
        string sessionExternalId)
    {
        EnsureActive();
        port = RequirePort(port);
        sessionExternalId = RequireSessionId(sessionExternalId);
        lock (sync)
        {
            EnsureInitializedLocked();
            if (!registrations.TryGetValue(port, out var registration) ||
                registration.Generation != generation)
            {
                return false;
            }
            sessions[sessionExternalId] = new(
                port,
                generation,
                checked(++nextSessionSequence));
            PruneSessionsLocked();
            return true;
        }
    }

    public bool ForgetSession(
        int port,
        long generation,
        string sessionExternalId)
    {
        EnsureActive();
        port = RequirePort(port);
        sessionExternalId = RequireSessionId(sessionExternalId);
        lock (sync)
        {
            EnsureInitializedLocked();
            if (!sessions.TryGetValue(sessionExternalId, out var mapping) ||
                mapping.Port != port ||
                mapping.Generation != generation)
            {
                return false;
            }
            sessions.Remove(sessionExternalId);
            return true;
        }
    }

    public ValueTask<long> WaitForChangeAsync(
        long observedRevision,
        CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            if (revision != observedRevision)
            {
                return ValueTask.FromResult(revision);
            }
            return new(changed.Task.WaitAsync(cancellationToken));
        }
    }

    private bool RemoveRegistrationLocked(int port, long generation)
    {
        if (!registrations.TryGetValue(port, out var registration) ||
            registration.Generation != generation)
        {
            return false;
        }
        registrations.Remove(port);
        RemoveSessionsLocked(port);
        SignalChangedLocked();
        return true;
    }

    private void RemoveSessionsLocked(int port)
    {
        foreach (var sessionId in sessions
            .Where(item => item.Value.Port == port)
            .Select(item => item.Key)
            .ToArray())
        {
            sessions.Remove(sessionId);
        }
    }

    private void PruneSessionsLocked()
    {
        while (sessions.Count > sessionCapacity)
        {
            var oldest = sessions.MinBy(item => item.Value.Sequence);
            sessions.Remove(oldest.Key);
        }
    }

    private void SignalChangedLocked()
    {
        revision = checked(revision + 1);
        var previous = changed;
        changed = NewChangeSource();
        previous.TrySetResult(revision);
    }

    private BridgeOpenCodeEndpointDirectorySnapshot SnapshotLocked() => new(
        initialized,
        revision,
        registrations.Count,
        registrations.Values.Count(registration => registration.Ready),
        sessions.Count);

    private static BridgeOpenCodeEndpointIdentity Identity(
        Registration registration) => new(
            registration.Port,
            registration.Cwd,
            registration.Generation,
            registration.Ready);

    private void EnsureActive()
    {
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            throw new InvalidOperationException(
                "OpenCode 生产端点目录只能用于 Active Host。");
        }
    }

    private void EnsureInitializedLocked()
    {
        if (!initialized)
        {
            throw new InvalidOperationException("OpenCode 端点目录尚未初始化。");
        }
    }

    private static int RequirePort(int port)
    {
        if (port is <= 0 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "OpenCode 端口无效。");
        }
        return port;
    }

    private static string NormalizeCwd(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd) ||
            cwd.Length > 32_768 ||
            cwd.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(cwd.Trim()))
        {
            throw new ArgumentException("OpenCode 工作目录无效。", nameof(cwd));
        }
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd.Trim()));
        }
        catch (Exception error) when (
            error is ArgumentException or IOException or NotSupportedException)
        {
            throw new ArgumentException("OpenCode 工作目录无效。", nameof(cwd), error);
        }
    }

    private static string RequireSessionId(string sessionExternalId)
    {
        if (!ValidSessionId(sessionExternalId))
        {
            throw new ArgumentException(
                "OpenCode 会话身份无效。",
                nameof(sessionExternalId));
        }
        return sessionExternalId;
    }

    private static bool ValidSessionId(string? sessionExternalId) =>
        !string.IsNullOrWhiteSpace(sessionExternalId) &&
        sessionExternalId.Length <= 512 &&
        !sessionExternalId.Any(char.IsControl);

    private static TaskCompletionSource<long> NewChangeSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record Registration(
        int Port,
        string Cwd,
        long Generation,
        bool Ready);

    private sealed record SessionMapping(
        int Port,
        long Generation,
        long Sequence);
}
