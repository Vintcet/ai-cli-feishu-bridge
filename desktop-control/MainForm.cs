using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AiCliFeishuControl;

internal sealed partial class MainForm : Form
{
    private const int SwRestore = 9;
    private const int IdaniCaption = 0x0003;

    private static readonly Color PageBackground = Color.FromArgb(244, 247, 251);
    private static readonly Color HeaderBackground = Color.FromArgb(15, 23, 42);
    private static readonly Color Primary = Color.FromArgb(37, 99, 235);
    private static readonly Color Success = Color.FromArgb(22, 163, 74);
    private static readonly Color Warning = Color.FromArgb(217, 119, 6);
    private static readonly Color Danger = Color.FromArgb(220, 38, 38);
    private static readonly Color OpenCodeAccent = Color.FromArgb(234, 88, 12);
    private static readonly Color Muted = Color.FromArgb(100, 116, 139);
    private static readonly Color Border = Color.FromArgb(226, 232, 240);

    private readonly BridgeClient bridgeClient = new();
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 15_000 };
    private readonly CancellationTokenSource lifetime = new();
    private readonly Font gridBoldFont = new("Microsoft YaHei UI", 9F, FontStyle.Bold);
    private readonly EventWaitHandle activateEvent;
    private readonly RegisteredWaitHandle activationRegistration;
    private readonly System.Windows.Forms.Timer activationTimer = new() { Interval = 200 };
    private readonly NotifyIcon trayIcon = new();
    private readonly ContextMenuStrip trayMenu = new();

    private readonly Label headerStatusLabel = new();
    private readonly Panel headerStatusDot = new();
    private readonly Label serviceValue = new();
    private readonly Label feishuValue = new();
    private readonly Label bindingsValue = new();
    private readonly Label sessionsValue = new();
    private readonly Label operationLabel = new();
    private readonly Label lastRefreshLabel = new();
    private readonly DataGridView sessionGrid = new();
    private readonly DataGridView historyGrid = new();
    private readonly TabControl sessionTabs = new();
    private readonly Button connectionButton = new();
    private readonly Button newCodexButton = new();
    private readonly Button newClaudeCodeButton = new();
    private readonly Button newOpenCodeButton = new();
    private readonly Button approvalButton = new();
    private readonly Button aliasButton = new();
    private readonly Button retryGroupButton = new();
    private readonly Button resumeSessionButton = new();
    private readonly Button deleteHistoryButton = new();
    private readonly Button settingsButton = new();
    private readonly Button refreshButton = new();
    private readonly Button folderButton = new();

    private bool refreshing;
    private bool operating;

    // Launching a runtime can take minutes. It blocks the launch entries like an
    // exclusive operation, but unlike `operating` it must not stop the refresh cycle,
    // otherwise sessions and approvals freeze for the whole startup.
    private bool launching;
    private bool closing;
    private bool exitRequested;
    private bool trayHintShown;
    private int activationRequested;
    private string? lastProjectDirectory;
    private BridgeStatus? lastStatus;
    private ApprovalDialog? approvalDialog;
    private readonly HashSet<string> dismissedApprovalIds = [];

    public MainForm(EventWaitHandle activateEvent)
    {
        this.activateEvent = activateEvent;
        try
        {
            AppLog.Initialize(Path.Combine(bridgeClient.BridgeRoot, "data"));
            AppLog.Info(
                $"面板启动 bridgeRoot={bridgeClient.BridgeRoot} " +
                $"port={bridgeClient.Port} " +
                $"host={bridgeClient.HostDisplayName} " +
                $"controlToken存在={File.Exists(Path.Combine(bridgeClient.BridgeRoot, "data", "control-token.json"))}");
        }
        catch (Exception error)
        {
            AppLog.Error("面板启动日志初始化异常", error);
        }
        Text = "AI CLI 飞书助手";
        Icon = LoadApplicationIcon();
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1280, 760);
        MinimumSize = new Size(1040, 640);
        BackColor = PageBackground;
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        AutoScaleMode = AutoScaleMode.Dpi;

        BuildLayout();
        ConfigureTrayIcon();
        activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            activateEvent,
            (_, timedOut) =>
            {
                if (!timedOut)
                {
                    Interlocked.Exchange(ref activationRequested, 1);
                }
            },
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
        activationTimer.Tick += (_, _) =>
        {
            if (Interlocked.Exchange(ref activationRequested, 0) == 1)
            {
                RestoreFromTray();
            }
        };
        activationTimer.Start();
        refreshTimer.Tick += async (_, _) => await RefreshStatusAsync();
        Shown += async (_, _) =>
        {
            RestoreFromTray();
            AppLog.Info("面板已显示，开始刷新 C# Bridge Host 状态。");
            SetOperating(true, "正在读取桥接状态…");
            try
            {
                await RefreshStatusAsync(force: true);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                AppLog.Error("首次刷新桥接状态失败", error);
            }
            finally
            {
                SetOperating(false);
            }
            refreshTimer.Start();
        };
        FormClosing += (_, eventArgs) =>
        {
            if (!exitRequested && eventArgs.CloseReason == CloseReason.UserClosing)
            {
                eventArgs.Cancel = true;
                HideToTray(showHint: true);
            }
        };
        FormClosed += (_, _) =>
        {
            closing = true;
            approvalDialog?.MarkResolved();
            refreshTimer.Stop();
            lifetime.Cancel();
            bridgeClient.Dispose();
            lifetime.Dispose();
            gridBoldFont.Dispose();
            activationRegistration.Unregister(null);
            activationTimer.Stop();
            activationTimer.Dispose();
            trayIcon.Visible = false;
            trayIcon.Dispose();
            trayMenu.Dispose();
        };
    }

    private void SyncApprovalDialog(BridgeStatus status)
    {
        var now = DateTimeOffset.Now;
        var pending = status.Approvals
            .Where(item =>
                item.Status == "pending" &&
                item.RequiresManualApproval &&
                item.DesktopApprovalRequested &&
                !IsExpiredApproval(item, now))
            .OrderBy(item => ParseTime(item.CreatedAt))
            .ToList();
        approvalButton.Text = pending.Count > 0
            ? $"本机审批 ({pending.Count})"
            : "本机审批";
        approvalButton.Enabled =
            !operating && bridgeClient.IsProductionTarget && pending.Count > 0;

        if (!bridgeClient.IsProductionTarget)
        {
            return;
        }

        dismissedApprovalIds.RemoveWhere(
            requestId => pending.All(item => item.RequestId != requestId));

        if (approvalDialog is not null)
        {
            var current = status.Approvals.FirstOrDefault(
                item => item.RequestId == approvalDialog.RequestId);
            if (current is null ||
                current.Status != "pending" ||
                IsExpiredApproval(current, now))
            {
                operationLabel.Text = current is not null && IsExpiredApproval(current, now)
                    ? ApprovalResolutionMessage("timeout")
                    : ApprovalResolutionMessage(current?.Resolution);
                var dialog = approvalDialog;
                approvalDialog = null;
                dialog.MarkResolved();
            }
        }

        if (approvalDialog is null)
        {
            var next = pending.FirstOrDefault(
                item => !dismissedApprovalIds.Contains(item.RequestId));
            if (next is not null)
            {
                ShowApprovalDialog(next);
            }
        }
    }

    private void OpenPendingApproval()
    {
        if (approvalDialog is not null)
        {
            approvalDialog.Show();
            approvalDialog.BringToFront();
            approvalDialog.Activate();
            return;
        }
        if (lastStatus is null)
        {
            return;
        }
        dismissedApprovalIds.Clear();
        SyncApprovalDialog(lastStatus);
    }

    private void ShowApprovalDialog(BridgeApproval approval)
    {
        if (closing || approvalDialog is not null)
        {
            return;
        }

        var dialog = new ApprovalDialog(
            approval,
            resolution => ResolveApprovalAsync(approval, resolution));
        dialog.Dismissed += (_, _) => dismissedApprovalIds.Add(approval.RequestId);
        dialog.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(approvalDialog, dialog))
            {
                approvalDialog = null;
            }
            if (!closing && !IsDisposed)
            {
                BeginInvoke(() =>
                {
                    if (lastStatus is not null)
                    {
                        SyncApprovalDialog(lastStatus);
                    }
                });
            }
            dialog.Dispose();
        };
        approvalDialog = dialog;
        RestoreFromTray();
        dialog.Show(this);
        dialog.BringToFront();
        dialog.Activate();
    }

    private async Task<ApprovalResolveResult> ResolveApprovalAsync(
        BridgeApproval approval,
        string resolution)
    {
        ApprovalResolveResult result;
        try
        {
            result = await bridgeClient.ResolveApprovalAsync(
                approval.RequestId,
                resolution,
                lifetime.Token);
        }
        catch (Exception) when (!lifetime.IsCancellationRequested)
        {
            BridgeStatus? refreshed = null;
            try
            {
                refreshed = await bridgeClient.GetStatusAsync(
                    lifetime.Token,
                    forceRefresh: true);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }

            if (refreshed is not null)
            {
                lastStatus = refreshed;
                var current = refreshed.Approvals.FirstOrDefault(
                    item => item.RequestId == approval.RequestId);
                if (current is null ||
                    current.Status != "pending" ||
                    IsExpiredApproval(current, DateTimeOffset.Now))
                {
                    var fallbackResolution = current?.Resolution;
                    if (string.IsNullOrWhiteSpace(fallbackResolution))
                    {
                        fallbackResolution = current is not null &&
                            IsExpiredApproval(current, DateTimeOffset.Now)
                                ? "timeout"
                                : "local";
                    }
                    result = new ApprovalResolveResult
                    {
                        Ok = true,
                        AlreadyResolved = true,
                        Resolution = fallbackResolution,
                        Message = "这条审批已经处理或失效。",
                    };
                }
                else
                {
                    throw;
                }
            }
            else
            {
                throw;
            }
        }
        if (lastStatus is not null)
        {
            var current = lastStatus.Approvals.FirstOrDefault(
                item => item.RequestId == approval.RequestId);
            if (current is not null && current.Status == "pending")
            {
                current.Status = "resolved";
                current.Resolution = result.Resolution;
                current.ResolvedAt = DateTime.UtcNow.ToString("O");
                lastStatus.PendingApprovals = Math.Max(0, lastStatus.PendingApprovals - 1);
                if (current.DesktopApprovalRequested)
                {
                    lastStatus.PendingDesktopApprovals =
                        Math.Max(0, lastStatus.PendingDesktopApprovals - 1);
                }
            }
        }
        operationLabel.Text = result.AlreadyResolved
            ? ApprovalResolutionMessage(result.Resolution)
            : resolution switch
            {
                "allow" => $"已在本机批准 {approval.SessionLabel} 的操作",
                "deny" => $"已在本机拒绝 {approval.SessionLabel} 的操作",
                _ => "审批已处理",
            };
        return result;
    }

    private static bool IsExpiredApproval(
        BridgeApproval approval,
        DateTimeOffset now) =>
        DateTimeOffset.TryParse(approval.ExpiresAt, out var expiresAt) &&
        expiresAt <= now;

    private static string ApprovalResolutionMessage(string? resolution) => resolution switch
    {
        "allow" => "审批已在飞书批准，目标助手正在继续执行",
        "deny" => "审批已在飞书拒绝，目标助手已停止这次操作",
        "local" => "桥接服务曾中断，这条审批已交还原 CLI 窗口",
        "timeout" => "审批等待超时，已交还原 CLI 窗口",
        _ => "这条审批已在另一端处理",
    };

    private static string SessionDisplayName(AssistantSession session) =>
        string.IsNullOrWhiteSpace(session.Alias)
            ? $"{session.ProjectName} #{session.ShortId}"
            : $"@{session.Alias}";

    private static string ShortSessionId(string sessionId) =>
        sessionId.Length <= 8 ? sessionId : sessionId[^8..];

    private static void RestoreGridState(
        DataGridView grid,
        string? selectedSessionId,
        int firstDisplayedRowIndex)
    {
        DataGridViewRow? selectedRow = null;
        if (!string.IsNullOrWhiteSpace(selectedSessionId))
        {
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.Tag is AssistantSession session &&
                    session.SessionId == selectedSessionId)
                {
                    selectedRow = row;
                    break;
                }
            }
            if (selectedRow is null)
            {
                return;
            }
        }

        if (firstDisplayedRowIndex >= 0 && firstDisplayedRowIndex < grid.Rows.Count)
        {
            grid.FirstDisplayedScrollingRowIndex = firstDisplayedRowIndex;
        }
        if (selectedRow is null)
        {
            return;
        }

        grid.ClearSelection();
        grid.CurrentCell = selectedRow.Cells[0];
        selectedRow.Selected = true;
    }

    private static void ConfigureButton(
        Button button,
        string text,
        Color background,
        Color foreground,
        Color? border = null)
    {
        button.Text = text;
        button.AutoSize = false;
        button.Size = new Size(94, 38);
        button.Margin = new Padding(8, 0, 0, 0);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = border ?? background;
        button.BackColor = background;
        button.ForeColor = foreground;
        button.Cursor = Cursors.Hand;
        button.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
    }

    private static string FeishuStateLabel(string state) => state switch
    {
        "connected" => "已连接",
        "connecting" => "连接中",
        "reconnecting" => "重连中",
        "failed" => "连接失败",
        _ => "未连接",
    };

    private static string SessionModeLabel(AssistantSession session)
    {
        var profile = RuntimeCatalog.FromId(session.Runtime);
        var runtime = profile.ShortName;
        if (!profile.UsesManagedTerminal) return runtime;
        if (!session.ManagedTerminal) return $"{runtime} 外部";
        return session.ManagedTerminalElevated
            ? $"{runtime} 管理员"
            : $"{runtime} 同步";
    }

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.TryParse(value, out var time) ? time : DateTimeOffset.MinValue;

    private static string FormatTime(string value) =>
        DateTimeOffset.TryParse(value, out var time)
            ? time.ToLocalTime().ToString("MM-dd HH:mm:ss")
            : "—";

    private void ShowOperationError(string title, Exception error)
    {
        operationLabel.Text = $"{title}：{error.Message}";
        MessageBox.Show(
            this,
            error.Message,
            title,
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private static Region RoundedRegion(Rectangle rectangle, int radius)
    {
        using var path = RoundedPath(rectangle, radius);
        return new Region(path);
    }

    private static GraphicsPath RoundedPath(Rectangle rectangle, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        var arc = new Rectangle(rectangle.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = rectangle.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = rectangle.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = rectangle.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class CardPanel : Panel
    {
        public int CornerRadius { get; set; } = 12;
        public Color BorderColor { get; set; } = Color.LightGray;

        public CardPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            base.OnPaint(eventArgs);
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = RoundedPath(bounds, CornerRadius);
            using var pen = new Pen(BorderColor, 1F);
            eventArgs.Graphics.DrawPath(pen, path);
        }

        protected override void OnResize(EventArgs eventArgs)
        {
            base.OnResize(eventArgs);
            if (Width > 0 && Height > 0)
            {
                Region?.Dispose();
                Region = RoundedRegion(ClientRectangle, CornerRadius);
            }
        }
    }
}
