using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

public sealed class DetailsForm : Form
{
    private readonly Action<UserPreferences> savePreferences;
    private readonly Action requestCheck;
    private readonly Action<bool> setPaused;
    private readonly Action restorePrevious;
    private readonly Action<string, ServiceKind, DateTime> reportServiceFailure;
    private readonly Action exitApplication;
    private readonly Panel settingsPanel = new Panel();
    private readonly Panel statusPanel = new Panel();
    private readonly List<ServiceChoice> choices = new List<ServiceChoice>();
    private readonly CheckBox performanceOptimization = new CheckBox();
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

    public DetailsForm(UserPreferences preferences, Action<UserPreferences> savePreferences,
        Action requestCheck, Action<bool> setPaused, Action restorePrevious,
        Action startBrowserVerification,
        Action<string, ServiceKind, DateTime> reportServiceFailure, Action exitApplication)
    {
        this.savePreferences = savePreferences;
        this.requestCheck = requestCheck;
        this.setPaused = setPaused;
        this.restorePrevious = restorePrevious;
        this.reportServiceFailure = reportServiceFailure;
        this.exitApplication = exitApplication;

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
        settingsPanel.Dock = DockStyle.Fill;
        settingsPanel.Padding = new Padding(28);
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true };
        layout.Controls.Add(Heading("选择你长期需要使用的服务"));
        layout.Controls.Add(TextLabel("只需设置一次。程序会寻找同时兼容这些服务、长期稳定且综合体验更好的节点。"));

        AddChoice(layout, "ChatGPT", new[] { ServiceKind.ChatGPT }, preferences);
        AddChoice(layout, "Gemini", new[] { ServiceKind.Gemini }, preferences);
        AddChoice(layout, "Google", new[] { ServiceKind.Google }, preferences);
        AddChoice(layout, "GitHub", new[] { ServiceKind.GitHub }, preferences);
        AddChoice(layout, "Steam 商店、社区与 API", new[] { ServiceKind.SteamStore, ServiceKind.SteamCommunity, ServiceKind.SteamApi }, preferences);
        AddChoice(layout, "Discord", new[] { ServiceKind.Discord }, preferences);
        AddChoice(layout, "Spotify", new[] { ServiceKind.Spotify }, preferences);
        AddChoice(layout, "Epic", new[] { ServiceKind.Epic }, preferences);
        AddChoice(layout, "Z-Library 网页", new[] { ServiceKind.ZLibraryWeb }, preferences);
        performanceOptimization.Text = "当前节点可用时允许性能寻优（高级）";
        performanceOptimization.AutoSize = true;
        performanceOptimization.Checked = preferences.AutomaticOptimization;
        performanceOptimization.Margin = new Padding(0, 12, 0, 0);
        layout.Controls.Add(performanceOptimization);
        var save = new Button { Text = "保存设置并开始守护", AutoSize = true, Height = 38,
            Padding = new Padding(14, 4, 14, 4), BackColor = Color.FromArgb(45, 120, 240), ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 16, 0, 0) };
        save.FlatAppearance.BorderSize = 0;
        save.Click += delegate { SaveChoices(); };
        layout.Controls.Add(save);
        layout.Controls.Add(TextLabel("Steam 游戏与下载仍按 Clash 原有规则直连；这里显示的是服务端点响应时间。"));
        settingsPanel.Controls.Add(layout);
    }

    private void AddChoice(Control parent, string text, ServiceKind[] services, UserPreferences preferences)
    {
        var check = new CheckBox { Text = text, AutoSize = true, Checked = services.All(preferences.RequiredServices.Contains),
            Padding = new Padding(4), Margin = new Padding(0, 7, 0, 0), Font = new Font(Font, FontStyle.Bold) };
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
            AutomaticOptimization = performanceOptimization.Checked,
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
        Size scaledColumns = ScaleForCurrentDpi(110, 270);
        serviceList.Columns.Add("服务", scaledColumns.Width);
        serviceList.Columns.Add("实测状态", scaledColumns.Height);
        serviceList.Columns.Add("说明", ScaleForCurrentDpi(280, 0).Width);
        serviceList.Dock = DockStyle.Fill;
        serviceList.Margin = new Padding(0, 12, 0, 12);
        layout.Controls.Add(serviceList);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(ActionButton("立即复检", delegate { requestCheck(); }));
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
        buttons.Controls.Add(ActionButton("当前节点 Gemini 不可用", delegate { ReportCurrentFailure(ServiceKind.Gemini); }));
        buttons.Controls.Add(ActionButton("修改常用服务", delegate { ShowSettings(true); }));
        buttons.Controls.Add(ActionButton("切换记录与推荐", delegate { using (var window = new ExperienceForm()) window.ShowDialog(this); }));
        buttons.Controls.Add(ActionButton("运行统计", delegate { using (var window = new StatisticsForm()) window.ShowDialog(this); }));
        layout.Controls.Add(buttons);
        layout.Controls.Add(TextLabel("响应时间来自轻量 HTTP 探测，不代表网页渲染、AI 生成或游戏服务器延迟。"));
        layout.Controls.Add(ActionButton("退出节点守护", delegate { exitApplication(); }));
        statusPanel.Controls.Add(layout);
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
