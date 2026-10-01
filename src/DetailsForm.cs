using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

public static class ServiceTableLayout
{
    public static int[] ColumnWidths(int clientWidth, float dpiScale)
    {
        float scale = dpiScale > 0 ? dpiScale : 1F;
        int available = Math.Max(0, clientWidth - (int)Math.Ceiling(20 * scale));
        if (available == 0) return new[] { 0, 0, 0 };
        int[] minimums = {
            (int)Math.Ceiling(90 * scale),
            (int)Math.Ceiling(180 * scale),
            (int)Math.Ceiling(160 * scale)
        };
        int minimumTotal = minimums.Sum();
        if (minimumTotal >= available)
        {
            int first = (int)Math.Floor(available * minimums[0] / (double)minimumTotal);
            int second = (int)Math.Floor(available * minimums[1] / (double)minimumTotal);
            int third = available - first - second;
            return new[] { first, second, third };
        }

        int extra = available - minimumTotal;
        int serviceExtra = (int)Math.Floor(extra * 0.15);
        int statusExtra = (int)Math.Floor(extra * 0.35);
        return new[] {
            minimums[0] + serviceExtra,
            minimums[1] + statusExtra,
            minimums[2] + extra - serviceExtra - statusExtra
        };
    }
}

public sealed class DetailsForm : Form
{
    private readonly Action<UserPreferences> savePreferences;
    private readonly Action requestCheck;
    private readonly Action<bool> setPaused;
    private readonly Action restorePrevious;
    private readonly Action<string, ServiceKind, DateTime> reportServiceFailure;
    private readonly Action exitApplication;
    private readonly Action requestCandidateRanking;
    private readonly Action requestOptimization;
    private readonly bool legacyAutomaticOptimization;
    private readonly Panel settingsPanel = new Panel();
    private readonly Panel statusPanel = new Panel();
    private readonly List<ServiceChoice> choices = new List<ServiceChoice>();
    private readonly CheckBox automaticRecovery = new CheckBox();
    private readonly NumericUpDown checkInterval = new NumericUpDown();
    private readonly NumericUpDown failureThreshold = new NumericUpDown();
    private readonly NumericUpDown recoveryCooldown = new NumericUpDown();
    private readonly Label stateLabel = HeadingLabel();
    private readonly Label nodeLabel = new Label();
    private readonly Label decisionLabel = new Label();
    private readonly Label responseLabel = new Label();
    private readonly Label selectionLabel = new Label();
    private readonly Label nextCheckLabel = new Label();
    private readonly ListView serviceList = new ListView();
    private readonly Button pauseButton = new Button();
    private bool paused;
    private MonitorSnapshot latestSnapshot;
    private CandidateLatencyForm candidateLatencyForm;

    public DetailsForm(UserPreferences preferences, Action<UserPreferences> savePreferences,
        Action requestCheck, Action<bool> setPaused, Action restorePrevious,
        Action startBrowserVerification,
        Action<string, ServiceKind, DateTime> reportServiceFailure, Action exitApplication,
        Action requestCandidateRanking = null, Action requestOptimization = null)
    {
        this.savePreferences = savePreferences;
        this.requestCheck = requestCheck;
        this.setPaused = setPaused;
        this.restorePrevious = restorePrevious;
        this.reportServiceFailure = reportServiceFailure;
        this.exitApplication = exitApplication;
        this.requestCandidateRanking = requestCandidateRanking ?? delegate { };
        this.requestOptimization = requestOptimization ?? delegate { };
        legacyAutomaticOptimization = preferences.AutomaticOptimization;

        Text = "节点守护";
        Icon = AppIcon.Current;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = ScaleForCurrentDpi(720, 590);
        MinimumSize = ScaleForCurrentDpi(620, 500);
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = Color.FromArgb(246, 248, 251);

        BuildSettings(preferences);
        BuildStatus();
        Controls.Add(statusPanel);
        Controls.Add(settingsPanel);
        ShowSettings(!preferences.FirstRunComplete);
    }

    private Size ScaleForCurrentDpi(int width, int height)
    {
        using (var graphics = CreateGraphics())
        {
            return new Size((int)Math.Round(width * graphics.DpiX / 96F),
                (int)Math.Round(height * graphics.DpiY / 96F));
        }
    }

    private void BuildSettings(UserPreferences preferences)
    {
        preferences.RequiredServices = UserPreferencePolicy.NormalizeServices(preferences.RequiredServices);
        UserPreferencePolicy.NormalizeRecoverySettings(preferences);
        settingsPanel.Dock = DockStyle.Fill;
        settingsPanel.Padding = new Padding(28);
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true };
        layout.Controls.Add(Heading("持续全节点寻优设置"));
        layout.Controls.Add(TextLabel("每轮先测当前节点的网站延迟与实际出口；ChatGPT、Gemini 均不超过 800 ms 时保持节点。"));
        automaticRecovery.Text = "超出 800 ms 时自动寻优（固定启用）";
        automaticRecovery.AutoSize = true;
        automaticRecovery.Checked = true;
        automaticRecovery.Enabled = false;
        automaticRecovery.Margin = new Padding(0, 12, 0, 0);
        layout.Controls.Add(automaticRecovery);

        AddChoice(layout, "ChatGPT 与 Gemini（固定网站延迟检测）",
            CoreWebsitePolicy.Required, preferences, false);
        AddNumberSetting(layout, "检测间隔（秒）", checkInterval, 15, 3600, preferences.CheckIntervalSeconds);
        failureThreshold.Value = preferences.FailureThreshold;
        recoveryCooldown.Value = preferences.RecoveryCooldownMinutes;
        layout.Controls.Add(TextLabel("按 ChatGPT、Gemini 网站延迟和实际出口地区选节点；首个双核心均不超过 800 ms 的合格节点可切换，强制寻优仍扫描全部节点。网站延迟不代表登录或对话可用。"));
        layout.Controls.Add(Heading("其他服务诊断"));
        layout.Controls.Add(TextLabel("以下服务仅采集与展示，不作为自动切换门槛。已有历史数据继续保留。"));
        AddChoice(layout, "Google", new[] { ServiceKind.Google }, preferences);
        AddChoice(layout, "Steam API", new[] { ServiceKind.SteamApi }, preferences);
        AddChoice(layout, "GitHub", new[] { ServiceKind.GitHub }, preferences);
        AddChoice(layout, "Steam 商店与社区", new[] { ServiceKind.SteamStore, ServiceKind.SteamCommunity }, preferences);
        AddChoice(layout, "Discord", new[] { ServiceKind.Discord }, preferences);
        AddChoice(layout, "Spotify", new[] { ServiceKind.Spotify }, preferences);
        AddChoice(layout, "Epic", new[] { ServiceKind.Epic }, preferences);
        AddChoice(layout, "Z-Library 网页", new[] { ServiceKind.ZLibraryWeb }, preferences);
        var save = new Button { Text = "保存设置并开始守护", AutoSize = true, Height = 38,
            Padding = new Padding(14, 4, 14, 4), BackColor = Color.FromArgb(45, 120, 240), ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 16, 0, 0) };
        save.FlatAppearance.BorderSize = 0;
        save.Click += delegate { SaveChoices(); };
        layout.Controls.Add(save);
        layout.Controls.Add(TextLabel("Steam 游戏与下载仍按 Clash 原有规则直连；这里显示的是服务端点响应时间。"));
        settingsPanel.Controls.Add(layout);
    }

    private void AddNumberSetting(Control parent, string text, NumericUpDown input, int minimum, int maximum, int value)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 7, 0, 0) };
        row.Controls.Add(new Label { Text = text, AutoSize = true, Padding = new Padding(0, 4, 8, 0) });
        input.Minimum = minimum;
        input.Maximum = maximum;
        input.Value = value;
        input.Width = 90;
        row.Controls.Add(input);
        parent.Controls.Add(row);
    }

    private void AddChoice(Control parent, string text, ServiceKind[] services, UserPreferences preferences,
        bool enabled = true)
    {
        var check = new CheckBox { Text = text, AutoSize = true, Checked = services.All(preferences.RequiredServices.Contains),
            Enabled = enabled, Padding = new Padding(4), Margin = new Padding(0, 7, 0, 0),
            Font = new Font(Font, FontStyle.Bold) };
        choices.Add(new ServiceChoice(check, services));
        parent.Controls.Add(check);
    }

    private void SaveChoices()
    {
        var selected = choices.Where(x => x.CheckBox.Checked).SelectMany(x => x.Services).Distinct().ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "请至少选择一个服务。", "节点守护", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        savePreferences(new UserPreferences { FirstRunComplete = true,
            AutomaticOptimization = legacyAutomaticOptimization,
            AutomaticRecovery = true,
            CheckIntervalSeconds = (int)checkInterval.Value,
            FailureThreshold = (int)failureThreshold.Value,
            RecoveryCooldownMinutes = (int)recoveryCooldown.Value,
            RequiredServices = selected });
        ShowSettings(false);
    }

    private void BuildStatus()
    {
        statusPanel.Dock = DockStyle.Fill;
        statusPanel.Padding = new Padding(28);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 11 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(stateLabel);
        ConfigureText(nodeLabel, 15F, FontStyle.Bold, Color.FromArgb(31, 42, 58));
        nodeLabel.Padding = new Padding(0, 9, 0, 4);
        layout.Controls.Add(nodeLabel);
        ConfigureText(selectionLabel, 9F, FontStyle.Regular, Color.FromArgb(74, 102, 143));
        selectionLabel.Padding = new Padding(0, 0, 0, 3);
        layout.Controls.Add(selectionLabel);
        ConfigureText(decisionLabel, 9F, FontStyle.Regular, Color.FromArgb(88, 101, 122));
        decisionLabel.Padding = new Padding(0, 0, 0, 8);
        layout.Controls.Add(decisionLabel);
        ConfigureText(responseLabel, 9F, FontStyle.Bold, Color.FromArgb(45, 120, 90));
        responseLabel.Padding = new Padding(0, 0, 0, 5);
        layout.Controls.Add(responseLabel);
        ConfigureText(nextCheckLabel, 9F, FontStyle.Regular, Color.FromArgb(88, 101, 122));
        nextCheckLabel.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        layout.Controls.Add(nextCheckLabel);

        serviceList.View = View.Details;
        serviceList.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        serviceList.FullRowSelect = true;
        serviceList.GridLines = true;
        serviceList.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        serviceList.Columns.Add("服务", 1);
        serviceList.Columns.Add("实测状态", 1);
        serviceList.Columns.Add("说明", 1);
        serviceList.Dock = DockStyle.Fill;
        serviceList.Margin = new Padding(0, 12, 0, 12);
        serviceList.ClientSizeChanged += delegate { ApplyServiceColumnLayout(); };
        serviceList.FontChanged += delegate { ApplyServiceColumnLayout(); };
        Resize += delegate { ApplyServiceColumnLayout(); };
        layout.Controls.Add(serviceList);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(ActionButton("立即复检当前节点", delegate { requestCheck(); }));
        pauseButton.Text = "暂停节点守护";
        pauseButton.AutoSize = true;
        pauseButton.Click += delegate {
            paused = !paused;
            setPaused(paused);
            pauseButton.Text = paused ? "恢复节点守护" : "暂停节点守护";
        };
        buttons.Controls.Add(pauseButton);
        buttons.Controls.Add(ActionButton("恢复上一个节点", delegate { restorePrevious(); }));
        buttons.Controls.Add(ActionButton("当前节点 ChatGPT 不可用", delegate { ReportCurrentFailure(ServiceKind.ChatGPT); }));
        buttons.Controls.Add(ActionButton("故障恢复设置", delegate { ShowSettings(true); }));
        buttons.Controls.Add(ActionButton("切换记录与推荐", delegate { using (var window = new ExperienceForm()) window.ShowDialog(this); }));
        buttons.Controls.Add(ActionButton("运行统计", delegate { using (var window = new StatisticsForm()) window.ShowDialog(this); }));
        buttons.Controls.Add(ActionButton("候选网站延迟排行", delegate { ShowCandidateLatencyRanking(); }));
        buttons.Controls.Add(ActionButton("立即开始一轮全节点寻优", delegate { requestOptimization(); }));
        layout.Controls.Add(buttons);
        layout.Controls.Add(TextLabel("响应时间来自轻量 HTTP 探测，不代表网页渲染、AI 生成或游戏服务器延迟。"));
        layout.Controls.Add(ActionButton("退出节点守护", delegate { exitApplication(); }));
        statusPanel.Controls.Add(layout);
    }

    private void ApplyServiceColumnLayout()
    {
        if (serviceList.Columns.Count != 3 || serviceList.ClientSize.Width <= 0) return;
        float scale;
        using (var graphics = serviceList.CreateGraphics()) scale = graphics.DpiX / 96F;
        int[] widths = ServiceTableLayout.ColumnWidths(serviceList.ClientSize.Width, scale);
        for (int i = 0; i < widths.Length; i++) serviceList.Columns[i].Width = widths[i];
    }

    public void UpdateSnapshot(MonitorSnapshot snapshot)
    {
        if (snapshot == null || IsDisposed) return;
        latestSnapshot = snapshot;
        MonitorPresentation view = MonitorPresentation.From(snapshot);
        stateLabel.Text = view.StateText;
        nodeLabel.Text = view.NodeText;
        decisionLabel.Text = view.DecisionText;
        responseLabel.Text = view.ResponseText;
        selectionLabel.Text = String.IsNullOrWhiteSpace(snapshot.SelectionReason) ? "" : "当前节点来源：" + snapshot.SelectionReason;
        nextCheckLabel.Text = "最近检测：" + (snapshot.CheckedUtc == DateTime.MinValue ? "尚未完成" : snapshot.CheckedUtc.ToLocalTime().ToString("MM-dd HH:mm:ss")) +
            (snapshot.NextCheckUtc == DateTime.MaxValue ? "" : " · 下次检查：" + snapshot.NextCheckUtc.ToLocalTime().ToString("HH:mm:ss"));
        paused = snapshot.State == MonitorRunState.Paused;
        pauseButton.Text = paused ? "恢复节点守护" : "暂停节点守护";
        serviceList.BeginUpdate();
        serviceList.Items.Clear();
        foreach (ServiceMeasurement service in snapshot.Services)
        {
            var item = new ListViewItem(MonitorPresentation.ServiceLabel(service.Service));
            item.SubItems.Add(MonitorPresentation.ServiceText(service));
            item.SubItems.Add(service.Detail);
            serviceList.Items.Add(item);
        }
        serviceList.EndUpdate();
        if (candidateLatencyForm != null && !candidateLatencyForm.IsDisposed)
            candidateLatencyForm.UpdateSnapshot(snapshot);
    }

    private void ShowCandidateLatencyRanking()
    {
        if (candidateLatencyForm == null || candidateLatencyForm.IsDisposed)
        {
            candidateLatencyForm = new CandidateLatencyForm(latestSnapshot, requestCandidateRanking,
                requestOptimization);
            candidateLatencyForm.FormClosed += delegate { candidateLatencyForm = null; };
        }
        candidateLatencyForm.UpdateSnapshot(latestSnapshot);
        candidateLatencyForm.Show(this);
        if (candidateLatencyForm.WindowState == FormWindowState.Minimized)
            candidateLatencyForm.WindowState = FormWindowState.Normal;
        candidateLatencyForm.Activate();
        if (latestSnapshot == null || latestSnapshot.CandidateLatencies == null ||
            latestSnapshot.CandidateLatencies.Count == 0)
            requestCandidateRanking();
    }

    private void ReportCurrentFailure(ServiceKind service)
    {
        if (latestSnapshot == null || String.IsNullOrWhiteSpace(latestSnapshot.ActualNode))
        {
            MessageBox.Show(this, "当前还没有可反馈的节点。", "节点守护",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        reportServiceFailure(latestSnapshot.ActualNode, service, DateTime.UtcNow);
    }

    private void ShowSettings(bool value)
    {
        settingsPanel.Visible = value;
        statusPanel.Visible = !value;
        if (value) settingsPanel.BringToFront(); else statusPanel.BringToFront();
    }

    private static Label Heading(string text) { var label = HeadingLabel(); label.Text = text; return label; }
    private static Label HeadingLabel() { var label = new Label(); ConfigureText(label, 18F, FontStyle.Bold, Color.FromArgb(24, 33, 47)); return label; }
    private static Label TextLabel(string text) { var label = new Label(); ConfigureText(label, 9F, FontStyle.Regular, Color.FromArgb(90, 103, 124)); label.Text = text; label.MaximumSize = new Size(630, 0); return label; }
    private static void ConfigureText(Label label, float size, FontStyle style, Color color) { label.AutoSize = true; label.Font = new Font("Microsoft YaHei UI", size, style); label.ForeColor = color; }
    private static Button ActionButton(string text, EventHandler handler) { var button = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 4, 8, 4) }; button.Click += handler; return button; }

    private sealed class ServiceChoice
    {
        public ServiceChoice(CheckBox checkBox, ServiceKind[] services) { CheckBox = checkBox; Services = services; }
        public CheckBox CheckBox { get; private set; }
        public ServiceKind[] Services { get; private set; }
    }
}
