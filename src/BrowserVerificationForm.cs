using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

public enum BrowserVerificationChoice
{
    None,
    EnableAndStart,
    StartOnce,
    Revoke,
    OpenSetup
}

public sealed class BrowserVerificationForm : Form
{
    public BrowserVerificationChoice Choice { get; private set; }

    public BrowserVerificationForm(string node, IEnumerable<ServiceKind> services,
        bool companionOnline, string companionDescription, bool consent, bool currentReady)
    {
        Text = "自动实测当前节点";
        Icon = AppIcon.Current;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(600, 390);
        MinimumSize = new Size(560, 360);
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.FromArgb(246, 248, 251);

        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, Padding = new Padding(24) };
        layout.Controls.Add(Label(ActionText(companionOnline, currentReady), 14F, FontStyle.Bold));
        layout.Controls.Add(Label("当前节点：" + (node ?? ""), 10F, FontStyle.Bold));
        string selected = String.Join("、", (services ?? new ServiceKind[0])
            .Where(x => x == ServiceKind.ChatGPT || x == ServiceKind.Gemini)
            .Select(MonitorPresentation.ServiceLabel));
        layout.Controls.Add(Label("实测服务：" + (String.IsNullOrEmpty(selected) ? "没有选中的 AI 服务" : selected),
            9F, FontStyle.Regular));
        layout.Controls.Add(Label("浏览器伴侣：" + (companionDescription ?? (companionOnline ? "已连接" : "未连接")),
            9F, companionOnline ? FontStyle.Bold : FontStyle.Regular));
        layout.Controls.Add(Label("权限范围仅为本机消息，以及 chatgpt.com 和 gemini.google.com 两个官方站点。",
            9F, FontStyle.Regular));
        layout.Controls.Add(Label("通过后凭证仅对当前节点与加密出口指纹有效，最长 30 天；出口变化会立即失效。",
            9F, FontStyle.Regular));
        layout.Controls.Add(Label("测试会自动发送一条仅含随机口令的短消息并关闭标签页；测试对话会保留，不自动删除。",
            9F, FontStyle.Bold));

        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = true,
            Margin = new Padding(0, 15, 0, 0) };
        var primary = Button(companionOnline ? "启用并开始自动实测" : "打开浏览器伴侣安装说明",
            delegate { Complete(companionOnline ? BrowserVerificationChoice.EnableAndStart : BrowserVerificationChoice.OpenSetup); });
        primary.Enabled = companionOnline ? currentReady : true;
        actions.Controls.Add(primary);
        var once = Button("仅本次自动实测", delegate { Complete(BrowserVerificationChoice.StartOnce); });
        once.Enabled = CanStartOnce(companionOnline, currentReady);
        actions.Controls.Add(once);
        var revoke = Button("撤销授权", delegate { Complete(BrowserVerificationChoice.Revoke); });
        revoke.Enabled = consent;
        actions.Controls.Add(revoke);
        actions.Controls.Add(Button("取消", delegate { DialogResult = DialogResult.Cancel; Close(); }));
        layout.Controls.Add(actions);
        Controls.Add(layout);
    }

    public static string ActionText(bool companionOnline, bool currentReady)
    {
        if (!currentReady) return "请先完成当前节点检测";
        return companionOnline ? "自动实测当前节点" : "请先安装或打开浏览器伴侣";
    }

    public static bool CanStart(bool consent, bool companionOnline, bool currentReady)
    {
        return consent && companionOnline && currentReady;
    }

    public static bool CanStartOnce(bool companionOnline, bool currentReady)
    {
        return companionOnline && currentReady;
    }

    private void Complete(BrowserVerificationChoice choice)
    {
        Choice = choice;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static Label Label(string text, float size, FontStyle style)
    {
        return new Label { Text = text, AutoSize = true, MaximumSize = new Size(540, 0),
            Font = new Font("Microsoft YaHei UI", size, style), Margin = new Padding(0, 5, 0, 5) };
    }

    private static Button Button(string text, EventHandler action)
    {
        var button = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 4, 8, 4),
            Padding = new Padding(6, 2, 6, 2) };
        button.Click += action;
        return button;
    }
}
