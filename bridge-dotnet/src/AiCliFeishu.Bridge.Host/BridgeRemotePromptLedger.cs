using System.Security.Cryptography;
using System.Text;

namespace AiCliFeishu.Bridge.Host;

internal enum BridgeRemotePromptKind
{
    Manual,
    AutomaticRetry,
}

internal sealed class BridgeRemotePromptLedger(TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private const int MaximumEntries = 500;
    private readonly object sync = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly LinkedList<Entry> entries = [];

    public void Remember(
        string sessionId,
        string prompt,
        BridgeRemotePromptKind kind)
    {
        var entry = new Entry(
            sessionId,
            Fingerprint(prompt),
            kind,
            clock.GetUtcNow());
        lock (sync)
        {
            Prune();
            entries.AddLast(entry);
            while (entries.Count > MaximumEntries)
            {
                entries.RemoveFirst();
            }
        }
    }

    public void Forget(
        string sessionId,
        string prompt,
        BridgeRemotePromptKind kind)
    {
        var fingerprint = Fingerprint(prompt);
        lock (sync)
        {
            var node = entries.First;
            while (node is not null)
            {
                if (node.Value.SessionId == sessionId &&
                    node.Value.Fingerprint == fingerprint &&
                    node.Value.Kind == kind)
                {
                    entries.Remove(node);
                    return;
                }
                node = node.Next;
            }
        }
    }

    public BridgeRemotePromptKind? TryConsume(string sessionId, string prompt)
    {
        var fingerprint = Fingerprint(prompt);
        lock (sync)
        {
            Prune();
            var node = entries.First;
            while (node is not null)
            {
                if (node.Value.SessionId == sessionId &&
                    node.Value.Fingerprint == fingerprint)
                {
                    var kind = node.Value.Kind;
                    entries.Remove(node);
                    return kind;
                }
                node = node.Next;
            }
            return null;
        }
    }

    private void Prune()
    {
        var cutoff = clock.GetUtcNow() - Lifetime;
        while (entries.First is { Value.At: var at } && at < cutoff)
        {
            entries.RemoveFirst();
        }
    }

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())));

    private sealed record Entry(
        string SessionId,
        string Fingerprint,
        BridgeRemotePromptKind Kind,
        DateTimeOffset At);
}
