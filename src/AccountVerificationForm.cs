using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

[Flags]
public enum AccountVerificationSelection
{
    None = 0,
    ChatGPT = 1,
    Gemini = 2
}

public sealed class AccountVerificationForm : Form
{
    public AccountVerificationSelection VerifiedSelection { get; private set; }
    public ServiceKind? FailedService { get; private set; }

    public AccountVerificationForm(string node)
    {
        Text = "验证当前节点的 AI 服务";
        Icon = AppIcon.Current;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(540, 290);
        MinimumSize = new Size(500, 280);
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.FromArgb(246, 248, 251);

        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, Padding = new Padding(24) };
        layout.Controls.Add(Label("当前节点：" + (node ?? ""), 13F, FontStyle.Bold));
        layout.Controls.Add(Label("先打开两个官方网页并完成登录，再分别发送一条最短测试消息。确认后，软件只保存验证结果、时间和加密出口指纹。", 9F, FontStyle.Regular));
        layout.Controls.Add(Label("不会读取浏览器、Cookie、账号、输入内容或 AI 回复。", 9F, FontStyle.Bold));

        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 14, 0, 0) };
        actions.Controls.Add(Button("打开 ChatGPT 和 Gemini", delegate { OpenOfficialPages(); }));
        actions.Controls.Add(Button("两个都正常", delegate { Complete(true, true, null); }));
        actions.Controls.Add(Button("仅 ChatGPT 失败", delegate { Complete(false, true, ServiceKind.ChatGPT); }));
        actions.Controls.Add(Button("仅 Gemini 失败", delegate { Complete(true, false, ServiceKind.Gemini); }));
        actions.Controls.Add(Button("取消", delegate { DialogResult = DialogResult.Cancel; Close(); }));
        layout.Controls.Add(actions);
        Controls.Add(layout);
        Shown += delegate { OpenOfficialPages(); };
    }

    public static AccountVerificationSelection SelectedServices(bool chatGpt, bool gemini)
    {
        AccountVerificationSelection result = AccountVerificationSelection.None;
        if (chatGpt) result |= AccountVerificationSelection.ChatGPT;
        if (gemini) result |= AccountVerificationSelection.Gemini;
        return result;
    }

    public static IEnumerable<ServiceKind> Services(AccountVerificationSelection selection)
    {
        if ((selection & AccountVerificationSelection.ChatGPT) != 0) yield return ServiceKind.ChatGPT;
        if ((selection & AccountVerificationSelection.Gemini) != 0) yield return ServiceKind.Gemini;
    }

    private void Complete(bool chatGpt, bool gemini, ServiceKind? failed)
    {
        VerifiedSelection = SelectedServices(chatGpt, gemini);
        FailedService = failed;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void OpenOfficialPages()
    {
        try
        {
            Open("https://chatgpt.com/");
            Open("https://gemini.google.com/app");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法打开浏览器：" + ex.Message, "节点守护",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void Open(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private static Label Label(string text, float size, FontStyle style)
    {
        return new Label { Text = text, AutoSize = true, MaximumSize = new Size(480, 0),
            Font = new Font("Microsoft YaHei UI", size, style), Margin = new Padding(0, 5, 0, 5) };
    }

    private static Button Button(string text, EventHandler action)
    {
        var button = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 4, 8, 4), Padding = new Padding(6, 2, 6, 2) };
        button.Click += action;
        return button;
    }
}
