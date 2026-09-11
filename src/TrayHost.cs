using System;
using System.Drawing;
using System.Windows.Forms;
using System.Linq;
using System.IO;
using System.Diagnostics;

public sealed class TrayHost : ApplicationContext
{
    private readonly MonitorCoordinator coordinator;
    private readonly UserPreferenceStore preferenceStore;
    private readonly NotifyIcon tray;
    private readonly Control dispatcher;
    private UserPreferences preferences;
    private DetailsForm details;
    private string lastNode;

    public TrayHost(MonitorCoordinator coordinator, UserPreferenceStore preferenceStore, UserPreferences preferences)
    {
        this.coordinator = coordinator;
        this.preferenceStore = preferenceStore;
        this.preferences = preferences;
        dispatcher = new Control();
        IntPtr dispatcherHandle = dispatcher.Handle;

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开详情", null, delegate { ShowDetails(); });
        menu.Items.Add("立即复检", null, delegate { coordinator.RequestCheck(); });
        menu.Items.Add("自动实测当前节点", null, delegate { ShowBrowserVerification(); });
        menu.Items.Add("当前节点 ChatGPT 不可用", null, delegate { ReportCurrentFailure(ServiceKind.ChatGPT); });
        menu.Items.Add("当前节点 Gemini 不可用", null, delegate { ReportCurrentFailure(ServiceKind.Gemini); });
        menu.Items.Add("暂停自动优化", null, delegate { coordinator.SetPaused(true); });
        menu.Items.Add("恢复自动优化", null, delegate { coordinator.SetPaused(false); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, delegate { ExitThread(); });

        tray = new NotifyIcon {
            Icon = AppIcon.Current,
            Text = "节点守护 · 正在启动",
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.DoubleClick += delegate { ShowDetails(); };
        coordinator.SnapshotChanged += OnSnapshotChanged;
        coordinator.AttentionRequired += OnAttentionRequired;
    }

    public void ShowInitialIfNeeded()
    {
        if (!preferences.FirstRunComplete) ShowDetails();
    }

    public void ShowDetailsFromAnyThread()
    {
        if (dispatcher.IsDisposed) return;
        dispatcher.BeginInvoke((Action)ShowDetails);
    }

    private void ShowDetails()
    {
        if (details == null || details.IsDisposed)
        {
            details = new DetailsForm(preferences, SavePreferences, coordinator.RequestCheck,
                coordinator.SetPaused, coordinator.RequestRestorePrevious,
                ShowBrowserVerification, coordinator.ReportServiceFailure, ExitThread);
            details.FormClosed += delegate { details = null; MemoryTrimmer.TrimIdleWorkingSet(); };
        }
        details.UpdateSnapshot(coordinator.Latest);
        details.Show();
        if (details.WindowState == FormWindowState.Minimized) details.WindowState = FormWindowState.Normal;
        details.Activate();
    }

    private void ShowBrowserVerification()
    {
        MonitorSnapshot snapshot = coordinator.Latest;
        bool currentReady = snapshot != null && !String.IsNullOrWhiteSpace(snapshot.ActualNode) &&
            !String.IsNullOrWhiteSpace(snapshot.ExitFingerprint);
        var aiServices = preferences.RequiredServices.Where(x =>
            x == ServiceKind.ChatGPT || x == ServiceKind.Gemini).ToList();
        using (var form = new BrowserVerificationForm(snapshot == null ? "" : snapshot.ActualNode,
            aiServices, coordinator.IsBrowserCompanionOnline, coordinator.BrowserCompanionDescription,
            preferences.BrowserConversationVerification, currentReady && aiServices.Count > 0))
        {
            if (form.ShowDialog() != DialogResult.OK) return;
            if (form.Choice == BrowserVerificationChoice.OpenSetup)
            {
                OpenBrowserSetupDocumentation();
                return;
            }
            if (form.Choice == BrowserVerificationChoice.Revoke)
            {
                SaveBrowserConsent(false);
                coordinator.RevokeBrowserVerificationConsent();
                tray.ShowBalloonTip(3500, "节点守护", "已撤销浏览器自动实测授权。", ToolTipIcon.Info);
                return;
            }
            if (form.Choice == BrowserVerificationChoice.EnableAndStart) SaveBrowserConsent(true);
            if (form.Choice == BrowserVerificationChoice.EnableAndStart ||
                form.Choice == BrowserVerificationChoice.StartOnce)
                ShowBrowserStartResult(coordinator.StartBrowserVerification(true));
        }
    }

    private void ShowBrowserStartResult(BrowserVerificationStart result)
    {
        string message;
        if (result == BrowserVerificationStart.Started) message = "已开始自动实测，完成后会自动关闭测试标签页。";
        else if (result == BrowserVerificationStart.CompanionOffline)
        {
            OpenBrowserSetupDocumentation();
            return;
        }
        else if (result == BrowserVerificationStart.Busy) message = "已有一项自动实测正在进行。";
        else message = "当前没有可自动实测的 AI 服务，请先完成节点检测。";
        tray.ShowBalloonTip(4000, "节点守护", message, ToolTipIcon.Info);
    }

    private void SaveBrowserConsent(bool enabled)
    {
        SavePreferences(new UserPreferences {
            FirstRunComplete = preferences.FirstRunComplete,
            AutomaticOptimization = preferences.AutomaticOptimization,
            BrowserConversationVerification = enabled,
            RequiredServices = new System.Collections.Generic.List<ServiceKind>(preferences.RequiredServices)
        });
    }

    private void OpenBrowserSetupDocumentation()
    {
        string path = Path.Combine(Application.StartupPath, "QUICKSTART.md");
        if (!File.Exists(path))
        {
            MessageBox.Show("未找到本地 QUICKSTART.md，请重新安装完整发布包。", "节点守护",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show("无法打开本地安装说明：" + ex.Message, "节点守护",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ReportCurrentFailure(ServiceKind service)
    {
        MonitorSnapshot snapshot = coordinator.Latest;
        if (snapshot == null || String.IsNullOrWhiteSpace(snapshot.ActualNode))
        {
            tray.ShowBalloonTip(4000, "节点守护", "当前还没有可反馈的节点。", ToolTipIcon.Info);
            return;
        }
        coordinator.ReportServiceFailure(snapshot.ActualNode, service, DateTime.UtcNow);
    }

    private void SavePreferences(UserPreferences value)
    {
        preferenceStore.Save(value);
        preferences = value;
        coordinator.UpdatePreferences(value);
    }

    private void OnSnapshotChanged(MonitorSnapshot snapshot)
    {
        if (dispatcher.IsDisposed) return;
        dispatcher.BeginInvoke((Action)delegate {
            MonitorPresentation view = MonitorPresentation.From(snapshot);
            string text = "节点守护 · " + view.StateText;
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
            if (!String.IsNullOrWhiteSpace(lastNode) && !String.IsNullOrWhiteSpace(snapshot.ActualNode) &&
                !String.Equals(lastNode, snapshot.ActualNode, StringComparison.Ordinal))
                tray.ShowBalloonTip(4000, "节点守护已自动切换", snapshot.ActualNode, ToolTipIcon.Info);
            if (!String.IsNullOrWhiteSpace(snapshot.ActualNode)) lastNode = snapshot.ActualNode;
            if (details != null && !details.IsDisposed) details.UpdateSnapshot(snapshot);
        });
    }

    private void OnAttentionRequired(MonitorSnapshot snapshot)
    {
        if (dispatcher.IsDisposed) return;
        dispatcher.BeginInvoke((Action)delegate {
            tray.ShowBalloonTip(5000, "节点守护需要注意", snapshot.Decision, ToolTipIcon.Warning);
        });
    }

    protected override void ExitThreadCore()
    {
        coordinator.SnapshotChanged -= OnSnapshotChanged;
        coordinator.AttentionRequired -= OnAttentionRequired;
        tray.Visible = false;
        tray.Dispose();
        if (details != null && !details.IsDisposed) details.Dispose();
        dispatcher.Dispose();
        base.ExitThreadCore();
    }
}
