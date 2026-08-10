using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AiCliFeishuControl;

internal sealed partial class MainForm
{
    private static Icon LoadApplicationIcon()
    {
        try
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath)
                ?? (Icon)SystemIcons.Application.Clone();
        }
        catch (Exception)
        {
            return (Icon)SystemIcons.Application.Clone();
        }
    }

    private void ConfigureTrayIcon()
    {
        var openItem = new ToolStripMenuItem("打开 AI CLI 飞书助手");
        openItem.Font = new Font(openItem.Font, FontStyle.Bold);
        openItem.Click += (_, _) => RestoreFromTray();
        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitFromTray();
        trayMenu.Items.Add(openItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(exitItem);

        trayIcon.Icon = Icon ?? SystemIcons.Application;
        trayIcon.Text = "AI CLI 飞书助手";
        trayIcon.ContextMenuStrip = trayMenu;
        trayIcon.Visible = true;
        trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void HideToTray(bool showHint)
    {
        if (Visible && WindowState != FormWindowState.Minimized)
        {
            var source = NativeRectangle.From(Bounds);
            var target = NativeRectangle.From(TrayAnimationTarget());
            DrawAnimatedRects(Handle, IdaniCaption, ref source, ref target);
        }
        Hide();
        ShowInTaskbar = false;
        if (showHint && !trayHintShown)
        {
            trayHintShown = true;
            trayIcon.ShowBalloonTip(
                2500,
                "AI CLI 飞书助手仍在运行",
                "双击托盘图标可重新打开；右键选择“退出”才会完全关闭。",
                ToolTipIcon.Info);
        }
    }

    private Rectangle TrayAnimationTarget()
    {
        var screen = Screen.PrimaryScreen ?? Screen.FromHandle(Handle);
        var scale = Math.Max(1F, DeviceDpi / 96F);
        var size = (int)Math.Round(16 * scale);
        var horizontalInset = (int)Math.Round(18 * scale);
        var verticalInset = (int)Math.Round(14 * scale);
        return new Rectangle(
            screen.Bounds.Right - horizontalInset - size,
            screen.Bounds.Bottom - verticalInset - size,
            size,
            size);
    }

    private void RestoreFromTray()
    {
        if (closing || IsDisposed)
        {
            return;
        }

        var currentBoundsAreVisible = IsOnScreen(Bounds);
        var restoreBounds = currentBoundsAreVisible
            ? Bounds
            : VisibleRestoreBounds();

        // Windows may leave a hidden minimized form at (-32000, -32000).
        // Restore its state and bounds before making it interactive again.
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }
        if (!currentBoundsAreVisible)
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = restoreBounds;
        }
        ShowInTaskbar = true;
        Show();
        if (!IsOnScreen(Bounds))
        {
            Bounds = restoreBounds;
        }
        ShowWindow(Handle, SwRestore);
        if (!SetForegroundWindow(Handle))
        {
            var wasTopMost = TopMost;
            TopMost = true;
            BringToFront();
            TopMost = wasTopMost;
        }
        else
        {
            BringToFront();
        }
        Activate();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawAnimatedRects(
        IntPtr windowHandle,
        int animation,
        ref NativeRectangle source,
        ref NativeRectangle target);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public static NativeRectangle From(Rectangle rectangle) => new()
        {
            Left = rectangle.Left,
            Top = rectangle.Top,
            Right = rectangle.Right,
            Bottom = rectangle.Bottom,
        };
    }

    private Rectangle VisibleRestoreBounds()
    {
        if (IsOnScreen(RestoreBounds))
        {
            return RestoreBounds;
        }

        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var width = Math.Min(Math.Max(MinimumSize.Width, Width), area.Width);
        var height = Math.Min(Math.Max(MinimumSize.Height, Height), area.Height);
        return new Rectangle(
            area.Left + Math.Max(0, (area.Width - width) / 2),
            area.Top + Math.Max(0, (area.Height - height) / 2),
            width,
            height);
    }

    private static bool IsOnScreen(Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return false;
        }

        return Screen.AllScreens.Any(screen =>
        {
            var visible = Rectangle.Intersect(screen.WorkingArea, bounds);
            return visible.Width >= Math.Min(160, bounds.Width) &&
                visible.Height >= Math.Min(80, bounds.Height);
        });
    }

    private void ExitFromTray()
    {
        exitRequested = true;
        Close();
    }

}
