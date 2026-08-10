using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal sealed partial class ActiveFeishuApprovalNotificationCoordinator
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started)
            {
                return;
            }
            started = true;
        }

        try
        {
            await SynchronizeAllBestEffortAsync(cancellationToken);
            lock (sync)
            {
                synchronizationLoop = RunSynchronizationLoopAsync(lifetime.Token);
            }
        }
        catch
        {
            lock (sync)
            {
                started = false;
            }
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? loop;
        lock (sync)
        {
            if (!started)
            {
                return;
            }
            started = false;
            lifetime.Cancel();
            loop = synchronizationLoop;
        }

        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }
        lock (sync)
        {
            synchronizationLoop = null;
        }
    }

    private async Task RunSynchronizationLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(SynchronizationInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await SynchronizeAllBestEffortAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task SynchronizeAllBestEffortAsync(CancellationToken cancellationToken)
    {
        await synchronizationGate.WaitAsync(cancellationToken);
        try
        {
            Interlocked.Increment(ref synchronizationRuns);
            var current = stateOwner.Snapshot;
            if (!current.Initialized)
            {
                return;
            }

            BridgeStoreSnapshot store;
            try
            {
                store = await storeOwner.ReadAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                Interlocked.Increment(ref synchronizationFailures);
                return;
            }

            foreach (var approval in current.Approvals.Requests.Values.Where(IsTerminal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!current.Sessions.Sessions.TryGetValue(
                        approval.SessionId,
                        out var session) ||
                    !TryStoredSession(session, store, out var storedSession))
                {
                    continue;
                }
                try
                {
                    await interactions.SynchronizeApprovalAsync(
                        approval,
                        SessionView(session, storedSession),
                        ApprovalView(approval, store),
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    Interlocked.Increment(ref synchronizationFailures);
                }
            }

            if (inputStateOwner is not null)
            {
                foreach (var input in current.Inputs.Requests.Values.Where(input =>
                             input.Status == InputRequestStatuses.Pending))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await NotifyPendingInputAsync(
                            input.RequestId,
                            input.SessionId,
                            cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        Interlocked.Increment(ref inputSynchronizationFailures);
                    }
                }

                foreach (var input in current.Inputs.Requests.Values.Where(IsTerminalInput))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await SynchronizeInputAsync(
                            input.RequestId,
                            input.SessionId,
                            cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        Interlocked.Increment(ref inputSynchronizationFailures);
                    }
                }
            }
        }
        finally
        {
            synchronizationGate.Release();
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
        }
        lifetime.Cancel();
        lifetime.Dispose();
        synchronizationGate.Dispose();
        inputSynchronizationGate.Dispose();
    }
}
