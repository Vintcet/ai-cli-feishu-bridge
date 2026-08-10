using System.Text.Json;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using AiCliFeishu.Bridge.Adapters.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AiCliFeishu.Bridge.Storage.Tests;

[TestClass]
public sealed class BridgeJsonStoreRepositoryTests
{
    [TestMethod]
    public async Task LoadsBridgeStoresAndProjectsCoreState()
    {
        await using var directory = await StoreTestDirectory.CreateAsync();
        var repository = new BridgeJsonStoreRepository(directory.Path);

        var snapshot = await repository.LoadAsync();
        var core = BridgeStoreCoreProjection.Project(snapshot);

        Assert.AreEqual(1, core.Sessions.Sessions.Count);
        Assert.AreEqual("opencode", core.Sessions.Sessions["session-1"].Runtime);
        Assert.AreEqual(1, core.Routes.Messages.Count);
        Assert.AreEqual(1, core.Approvals.Requests.Count);
    }

    [TestMethod]
    public async Task ReadOnlyRepositoryRejectsWrites()
    {
        await using var directory = await StoreTestDirectory.CreateAsync();
        var repository = new BridgeJsonStoreRepository(directory.Path);
        var snapshot = await repository.LoadAsync();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => repository.WriteAsync(snapshot));
    }

    [TestMethod]
    public async Task ActiveOwnerAccessUsesTheSameAtomicWritePath()
    {
        await using var directory = await StoreTestDirectory.CreateAsync();
        var repository = new BridgeJsonStoreRepository(
            directory.Path,
            BridgeStoreAccess.ReadWriteActiveOwner);
        var snapshot = await repository.LoadAsync();

        await repository.WriteAsync(snapshot);

        Assert.AreEqual(BridgeStoreAccess.ReadWriteActiveOwner, repository.Access);
        Assert.IsFalse(Directory.EnumerateFiles(directory.Path, "*.tmp").Any());
        Assert.AreEqual(
            "keep-root",
            (await repository.LoadAsync()).Sessions.ExtensionData!["futureRoot"].GetString());
    }

    [TestMethod]
    public async Task CopyRoundTripPreservesUnknownFields()
    {
        await using var directory = await StoreTestDirectory.CreateAsync();
        var repository = new BridgeJsonStoreRepository(
            directory.Path,
            BridgeStoreAccess.ReadWriteCopy);
        var snapshot = await repository.LoadAsync();

        await repository.WriteAsync(snapshot);
        var rewritten = await repository.LoadAsync();

        Assert.AreEqual(
            "keep-root",
            rewritten.Sessions.ExtensionData!["futureRoot"].GetString());
        Assert.AreEqual(
            "keep-session",
            rewritten.Sessions.Sessions["session-1"]
                .ExtensionData!["futureSession"].GetString());
        Assert.AreEqual(
            "keep-approval",
            rewritten.Approvals.Requests["approval-1"]
                .ExtensionData!["futureApproval"].GetString());
        Assert.IsFalse(Directory.EnumerateFiles(directory.Path, "*.tmp").Any());
    }

    [TestMethod]
    public async Task InvalidStoreIsRejectedWithoutRenamingOrOverwritingIt()
    {
        await using var directory = await StoreTestDirectory.CreateAsync();
        var sessions = System.IO.Path.Combine(directory.Path, "sessions.json");
        await File.WriteAllTextAsync(sessions, "{\"sessions\":{\"bad\":{}}}");
        var before = await File.ReadAllTextAsync(sessions);

        await Assert.ThrowsExceptionAsync<BridgeStoreValidationException>(
            () => new BridgeJsonStoreRepository(directory.Path).LoadAsync());

        Assert.AreEqual(before, await File.ReadAllTextAsync(sessions));
        Assert.IsTrue(File.Exists(sessions));
        Assert.AreEqual(0, Directory.EnumerateFiles(directory.Path, "*.corrupt-*").Count());
    }

    [TestMethod]
    public async Task WritableRepositoryQuarantinesInvalidStoreAndFailsClosed()
    {
        await using var directory = await StoreTestDirectory.CreateAsync();
        var sessions = System.IO.Path.Combine(directory.Path, "sessions.json");
        const string invalid = "{\"sessions\":{\"bad\":{}}}";
        await File.WriteAllTextAsync(sessions, invalid);
        var repository = new BridgeJsonStoreRepository(
            directory.Path,
            BridgeStoreAccess.ReadWriteActiveOwner);

        var error = await Assert.ThrowsExceptionAsync<BridgeStoreCorruptionException>(
            () => repository.LoadAsync());

        Assert.AreEqual("sessions.json", error.LogicalFile);
        Assert.IsFalse(File.Exists(sessions));
        var quarantined = Directory.EnumerateFiles(
            directory.Path,
            "sessions.json.corrupt-*").Single();
        Assert.AreEqual(invalid, await File.ReadAllTextAsync(quarantined));
    }

    [TestMethod]
    public async Task SemanticValidationRejectsMismatchedIdsUnknownStatusesAndBadTimestamps()
    {
        var cases = new[]
        {
            (
                "sessions.json",
                """
                {"sessions":{"session-1":{"sessionId":"different","cwd":"K:\\project","status":"waiting","lastSeenAt":"2026-08-06T00:01:00Z"}}}
                """,
                "必须与 map key"),
            (
                "sessions.json",
                """
                {"sessions":{"session-1":{"sessionId":"session-1","cwd":"K:\\project","status":"future-status","lastSeenAt":"2026-08-06T00:01:00Z"}}}
                """,
                "未知状态"),
            (
                "approvals.json",
                """
                {"requests":{"approval-1":{"requestId":"approval-1","sessionId":"session-1","turnId":"turn-1","cwd":"K:\\project","toolName":"shell_command","toolPreview":"summary","createdAt":"not-a-time","expiresAt":"2026-08-06T00:11:00Z","status":"pending","messageIds":[]}}}
                """,
                "createdAt 必须是有效时间戳"),
            (
                "message-routes.json",
                """
                {"messages":{"message-1":{"messageId":"message-1","sessionId":"session-1","chatId":"chat-1","kind":"approval","createdAt":"2026-08-06T00:01:00Z"}},"processedInbound":{"inbound-1":"not-a-time"}}
                """,
                "processedInbound.inbound-1 必须是有效时间戳"),
            (
                "sessions.json",
                """
                {"sessions":{"session-1":{"sessionId":"session-1","cwd":"K:\\project","status":"pending_input","lastSeenAt":"2026-08-06T00:01:00Z"}},"pendingInputs":"invalid"}
                """,
                "pendingInputs 必须是对象"),
        };

        foreach (var (fileName, invalid, expected) in cases)
        {
            await using var directory = await StoreTestDirectory.CreateAsync();
            await File.WriteAllTextAsync(Path.Combine(directory.Path, fileName), invalid);

            var error = await Assert.ThrowsExceptionAsync<BridgeStoreValidationException>(
                () => new BridgeJsonStoreRepository(directory.Path).LoadAsync());

            Assert.AreEqual(fileName, error.FileName);
            StringAssert.Contains(error.Message, expected);
        }
    }

    [TestMethod]
    public async Task WritableCrossFileValidationQuarantinesTheOffendingDocument()
    {
        var cases = new[]
        {
            (
                "approvals.json",
                """
                {"requests":{"approval-1":{"requestId":"approval-1","sessionId":"missing-session","turnId":"turn-1","cwd":"K:\\project","toolName":"shell_command","toolPreview":"summary","createdAt":"2026-08-06T00:01:00Z","expiresAt":"2026-08-06T00:11:00Z","status":"pending","messageIds":[]}}}
                """),
            (
                "message-routes.json",
                """
                {"messages":{"message-1":{"messageId":"message-1","sessionId":"missing-session","chatId":"chat-1","kind":"approval","createdAt":"2026-08-06T00:01:00Z"}},"processedInbound":{}}
                """),
            (
                "sessions.json",
                """
                {"sessions":{"session-1":{"sessionId":"session-1","cwd":"K:\\project","status":"pending_input","lastSeenAt":"2026-08-06T00:01:00Z"}},"pendingInputs":{"input-1":{"requestId":"input-1","sessionId":"missing-session","status":"pending","createdAt":"2026-08-06T00:01:00Z","expiresAt":"2026-08-06T00:11:00Z","questions":[{"id":"mode","multiple":false,"allowsCustom":false,"options":["safe"],"isSecret":false}],"answers":{}}}}
                """),
        };

        foreach (var (fileName, invalid) in cases)
        {
            await using var directory = await StoreTestDirectory.CreateAsync();
            var path = Path.Combine(directory.Path, fileName);
            await File.WriteAllTextAsync(path, invalid);
            var repository = new BridgeJsonStoreRepository(
                directory.Path,
                BridgeStoreAccess.ReadWriteActiveOwner);

            var error = await Assert.ThrowsExceptionAsync<BridgeStoreCorruptionException>(
                () => repository.LoadAsync());

            Assert.AreEqual(fileName, error.LogicalFile);
            Assert.IsFalse(File.Exists(path));
            Assert.AreEqual(
                1,
                Directory.EnumerateFiles(directory.Path, $"{fileName}.corrupt-*").Count());
        }
    }

    [TestMethod]
    public async Task WriteRejectsSemanticallyInvalidSnapshotBeforeCreatingACommit()
    {
        await using var directory = await StoreTestDirectory.CreateAsync();
        var repository = new BridgeJsonStoreRepository(
            directory.Path,
            BridgeStoreAccess.ReadWriteActiveOwner);
        var snapshot = await repository.LoadAsync();
        var current = snapshot.Sessions.Sessions["session-1"];
        var invalid = snapshot with
        {
            Sessions = new SessionStoreDocument
            {
                Sessions = new Dictionary<string, SessionStoreRecord>(StringComparer.Ordinal)
                {
                    ["session-1"] = new()
                    {
                        SessionId = current.SessionId,
                        Cwd = current.Cwd,
                        Status = "future-status",
                        LastSeenAt = current.LastSeenAt,
                    },
                },
            },
        };

        await Assert.ThrowsExceptionAsync<BridgeStoreValidationException>(
            () => repository.WriteAsync(invalid));

        Assert.IsFalse(File.Exists(repository.CommitManifestPath));
    }

    [TestMethod]
    public async Task FailureAfterNthPreparedFileKeepsPreviousCommittedGeneration()
    {
        await using var directory = await StoreTestDirectory.CreateAsync();
        var repository = new BridgeJsonStoreRepository(
            directory.Path,
            BridgeStoreAccess.ReadWriteActiveOwner);
        var original = await repository.LoadAsync();
        await repository.WriteAsync(original);
        var changed = original with
        {
            Sessions = new SessionStoreDocument
            {
                Sessions = new Dictionary<string, SessionStoreRecord>(
                    original.Sessions.Sessions,
                    StringComparer.Ordinal)
                {
                    ["session-1"] = new()
                    {
                        SessionId = "session-1",
                        Cwd = "K:\\changed",
                        Status = "running",
                        Runtime = "opencode",
                        LastSeenAt = "2026-08-06T00:03:00Z",
                    },
                },
            },
            Settings = new SettingsStoreDocument { NotifyActivity = true },
        };
        var interrupted = new BridgeJsonStoreRepository(
            directory.Path,
            BridgeStoreAccess.ReadWriteActiveOwner,
            checkpoint =>
            {
                if (checkpoint.Stage == "file-prepared" && checkpoint.Index == 2)
                {
                    throw new IOException("simulated interruption");
                }
            });

        await Assert.ThrowsExceptionAsync<IOException>(
            () => interrupted.WriteAsync(changed));

        var recovered = await new BridgeJsonStoreRepository(directory.Path).LoadAsync();
        Assert.AreEqual("K:\\project", recovered.Sessions.Sessions["session-1"].Cwd);
        Assert.AreNotEqual(true, recovered.Settings.NotifyActivity);
    }

    [TestMethod]
    public async Task CommittedManifestSurvivesMirrorInterruptionAndRepairsOnWritableLoad()
    {
        await using var directory = await StoreTestDirectory.CreateAsync();
        var repository = new BridgeJsonStoreRepository(
            directory.Path,
            BridgeStoreAccess.ReadWriteActiveOwner);
        var original = await repository.LoadAsync();
        await repository.WriteAsync(original);
        var changed = original with
        {
            Settings = new SettingsStoreDocument
            {
                WorkspaceRoot = "K:\\project",
                NotifyActivity = true,
            },
        };
        var interrupted = new BridgeJsonStoreRepository(
            directory.Path,
            BridgeStoreAccess.ReadWriteActiveOwner,
            checkpoint =>
            {
                if (checkpoint.Stage == "manifest-committed")
                {
                    throw new IOException("mirror was not reached");
                }
            });

        await Assert.ThrowsExceptionAsync<IOException>(
            () => interrupted.WriteAsync(changed));

        var canonical = await new BridgeJsonStoreRepository(directory.Path).LoadAsync();
        Assert.IsTrue(canonical.Settings.NotifyActivity);
        Assert.IsFalse((await File.ReadAllTextAsync(
            Path.Combine(directory.Path, "settings.json")))
            .Contains("\"notifyActivity\":true", StringComparison.Ordinal));

        var recovered = await new BridgeJsonStoreRepository(
            directory.Path,
            BridgeStoreAccess.ReadWriteActiveOwner).LoadAsync();
        Assert.IsTrue(recovered.Settings.NotifyActivity);
        StringAssert.Contains(
            await File.ReadAllTextAsync(Path.Combine(directory.Path, "settings.json")),
            "\"notifyActivity\":true");
    }

    [TestMethod]
    public async Task UnchangedSnapshotDoesNotRewriteManifestOrRootFiles()
    {
        await using var directory = await StoreTestDirectory.CreateAsync();
        var repository = new BridgeJsonStoreRepository(
            directory.Path,
            BridgeStoreAccess.ReadWriteActiveOwner);
        var snapshot = await repository.LoadAsync();
        await repository.WriteAsync(snapshot);
        var paths = BridgeStoreFile.All
            .Select(file => Path.Combine(directory.Path, file.FileName))
            .Append(repository.CommitManifestPath)
            .ToArray();
        var before = paths.ToDictionary(
            path => path,
            path => File.GetLastWriteTimeUtc(path),
            StringComparer.Ordinal);
        await Task.Delay(TimeSpan.FromMilliseconds(1100));

        await repository.WriteAsync(snapshot);

        foreach (var path in paths)
        {
            Assert.AreEqual(before[path], File.GetLastWriteTimeUtc(path), path);
        }
    }

    [TestMethod]
    public async Task ChangedSnapshotRewritesOnlyChangedRootMirror()
    {
        await using var directory = await StoreTestDirectory.CreateAsync();
        var initial = new BridgeJsonStoreRepository(
            directory.Path,
            BridgeStoreAccess.ReadWriteActiveOwner);
        var snapshot = await initial.LoadAsync();
        await initial.WriteAsync(snapshot);
        var mirrors = new List<BridgeStoreFile>();
        var repository = new BridgeJsonStoreRepository(
            directory.Path,
            BridgeStoreAccess.ReadWriteActiveOwner,
            checkpoint =>
            {
                if (checkpoint.Stage == "mirror-synchronized" && checkpoint.File is not null)
                {
                    mirrors.Add(checkpoint.File);
                }
            });

        await repository.WriteAsync(snapshot with
        {
            Settings = new SettingsStoreDocument
            {
                WorkspaceRoot = "K:\\project",
                NotifyActivity = true,
            },
        });

        CollectionAssert.AreEqual(
            new[] { BridgeStoreFile.Settings },
            mirrors.ToArray());
    }

    [TestMethod]
    public async Task CommittedObjectCorruptionIsQuarantinedAndFailsClosed()
    {
        await using var directory = await StoreTestDirectory.CreateAsync();
        var repository = new BridgeJsonStoreRepository(
            directory.Path,
            BridgeStoreAccess.ReadWriteActiveOwner);
        await repository.WriteAsync(await repository.LoadAsync());
        using var manifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(repository.CommitManifestPath));
        var objectName = manifest.RootElement
            .GetProperty("files")
            .GetProperty("sessions.json")
            .GetProperty("objectName")
            .GetString()!;
        var objectPath = Path.Combine(
            directory.Path,
            ".bridge-store",
            "objects",
            objectName);
        await File.WriteAllTextAsync(objectPath, "{\"sessions\":{\"broken\":{}}}");

        var error = await Assert.ThrowsExceptionAsync<BridgeStoreCorruptionException>(
            () => repository.LoadAsync());

        Assert.AreEqual("sessions.json", error.LogicalFile);
        Assert.IsFalse(File.Exists(objectPath));
        Assert.AreEqual(
            1,
            Directory.EnumerateFiles(
                Path.GetDirectoryName(objectPath)!,
                $"{objectName}.corrupt-*").Count());
    }

    [TestMethod]
    public async Task MissingCanonicalManifestRecoversHistoryInsteadOfLoadingMixedMirrors()
    {
        await using var directory = await StoreTestDirectory.CreateAsync();
        var repository = new BridgeJsonStoreRepository(
            directory.Path,
            BridgeStoreAccess.ReadWriteActiveOwner);
        var snapshot = await repository.LoadAsync();
        await repository.WriteAsync(snapshot with
        {
            Settings = new SettingsStoreDocument
            {
                WorkspaceRoot = "K:\\canonical",
                NotifyActivity = true,
            },
        });
        await File.WriteAllTextAsync(repository.CommitManifestPath, "{broken");

        await Assert.ThrowsExceptionAsync<BridgeStoreCorruptionException>(
            () => repository.LoadAsync());
        Assert.IsFalse(File.Exists(repository.CommitManifestPath));
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "settings.json"),
            "{\"workspaceRoot\":\"K:\\\\mixed-mirror\",\"notifyActivity\":false}");

        var recovered = await new BridgeJsonStoreRepository(
            directory.Path,
            BridgeStoreAccess.ReadWriteActiveOwner).LoadAsync();

        Assert.AreEqual("K:\\canonical", recovered.Settings.WorkspaceRoot);
        Assert.IsTrue(recovered.Settings.NotifyActivity);
        Assert.IsTrue(File.Exists(repository.CommitManifestPath));
        StringAssert.Contains(
            await File.ReadAllTextAsync(Path.Combine(directory.Path, "settings.json")),
            "K:\\\\canonical");
    }

    [TestMethod]
    public async Task WindowsStoreAndControlTokenAclExcludeBroadLocalGroups()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        await AssertWindowsAclAsync();
    }

    [SupportedOSPlatform("windows")]
    private static async Task AssertWindowsAclAsync()
    {
        await using var directory = await StoreTestDirectory.CreateAsync();
        var repository = new BridgeJsonStoreRepository(
            directory.Path,
            BridgeStoreAccess.ReadWriteActiveOwner);
        await repository.WriteAsync(await repository.LoadAsync());

        var broadSids = new HashSet<string>(StringComparer.Ordinal)
        {
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null).Value,
        };
        var directorySecurity = new DirectoryInfo(directory.Path).GetAccessControl();
        Assert.IsTrue(directorySecurity.AreAccessRulesProtected);
        Assert.IsFalse(HasAllowedIdentity(directorySecurity, broadSids));

        var tokenSecurity = new FileInfo(
            Path.Combine(directory.Path, "control-token.json")).GetAccessControl();
        Assert.IsTrue(tokenSecurity.AreAccessRulesProtected);
        Assert.IsFalse(HasAllowedIdentity(tokenSecurity, broadSids));
    }

    [SupportedOSPlatform("windows")]
    private static bool HasAllowedIdentity(
        FileSystemSecurity security,
        IReadOnlySet<string> identities) => security
            .GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Any(rule =>
                rule.AccessControlType == AccessControlType.Allow &&
                rule.IdentityReference is SecurityIdentifier sid &&
                identities.Contains(sid.Value));

    private sealed class StoreTestDirectory : IAsyncDisposable
    {
        private StoreTestDirectory(string path) => Path = path;

        public string Path { get; }

        public static async Task<StoreTestDirectory> CreateAsync()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"ai-cli-feishu-m2-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            var files = new Dictionary<string, string>
            {
                ["bindings.json"] = """
                    {"users":{"owner":{"openId":"owner","chatId":"chat-owner","chatType":"p2p","boundAt":"2026-08-06T00:00:00Z"}},"ownerOpenId":"owner"}
                    """,
                ["sessions.json"] = """
                    {"sessions":{"session-1":{"sessionId":"session-1","shortId":"session1","cwd":"K:\\project","projectName":"project","status":"waiting","runtime":"opencode","openedAt":"2026-08-06T00:00:00Z","lastSeenAt":"2026-08-06T00:01:00Z","futureSession":"keep-session"}},"futureRoot":"keep-root"}
                    """,
                ["message-routes.json"] = """
                    {"messages":{"message-1":{"messageId":"message-1","sessionId":"session-1","chatId":"chat-1","kind":"approval","createdAt":"2026-08-06T00:01:00Z","requestId":"approval-1"}},"processedInbound":{"inbound-1":"2026-08-06T00:02:00Z"}}
                    """,
                ["approvals.json"] = """
                    {"requests":{"approval-1":{"requestId":"approval-1","sessionId":"session-1","turnId":"turn-1","cwd":"K:\\project","toolName":"shell_command","toolPreview":"summary","createdAt":"2026-08-06T00:01:00Z","expiresAt":"2026-08-06T00:11:00Z","status":"pending","messageIds":["message-1"],"futureApproval":"keep-approval"}}}
                    """,
                ["settings.json"] = """
                    {"workspaceRoot":"K:\\project","notifyActivity":false,"retryMaxAttempts":3,"futureSetting":true}
                    """,
                ["control-token.json"] = """
                    {"token":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}
                    """,
            };
            foreach (var file in files)
            {
                await File.WriteAllTextAsync(System.IO.Path.Combine(path, file.Key), file.Value);
            }
            return new StoreTestDirectory(path);
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(Path, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
