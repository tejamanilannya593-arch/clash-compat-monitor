using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

public sealed class ExperienceForm : Form
{
    public ExperienceForm()
    {
        Text = "切换记录与历史数据";
        Icon = AppIcon.Current;
        Font = new Font("Microsoft YaHei UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        using (var graphics = CreateGraphics()) Size = new Size((int)(800 * graphics.DpiX / 96), (int)(540 * graphics.DpiY / 96));
        var tabs = new TabControl { Dock = DockStyle.Fill };
        Controls.Add(tabs);
        ExperienceData data;
        try { data = new ExperienceStore(Path.Combine(MonitorConfiguration.CreateDefault().RootPath, "state", "experience.json")).Load(); }
        catch (IOException) { data = new ExperienceData(); }
        var historyPage = new TabPage("切换记录");
        var history = Table("时间", "原节点", "新节点", "原因");
        foreach (var entry in data.Changes.OrderByDescending(x => x.Utc))
            history.Items.Add(new ListViewItem(new[] { entry.Utc.ToLocalTime().ToString("MM-dd HH:mm:ss"), entry.From, entry.To, entry.Reason }));
        historyPage.Controls.Add(history);
        historyPage.Controls.Add(Note(data.Changes.Count == 0 ? "暂无切换记录。首次启动只建立基线；以后记录确认成功的切换及检测到的外部变更。" : "最多保留 300 条。外部变更只能记录检测发现的变化，无法追溯两轮之间的每次手动操作。"));
        tabs.TabPages.Add(historyPage);
        var memoryPage = new TabPage("历史数据");
        var memory = Table("节点", "样本", "参考分", "严格成功率", "平均响应", "响应波动", "近期采样速度", "最近检测");
        var memories = data.Nodes.Where(x => x.Scope == data.ActiveScope && x.LastUtc >= DateTime.UtcNow.AddDays(-7))
            .OrderByDescending(x => x.Rank(DateTime.UtcNow)).ToList();
        foreach (var node in memories)
        {
            double strictRate = node.OutcomeSamples == 0 ? 0 : (double)node.SuccessfulSamples / node.OutcomeSamples;
            string samples = node.SuccessfulSamples + "/" + node.OutcomeSamples + " 次成功";
            memory.Items.Add(new ListViewItem(new[] { node.Node, samples, node.Rank(DateTime.UtcNow).ToString("F0"),
                strictRate.ToString("P0"), node.ResponseMs.ToString("F0") + " ms", node.JitterMs.ToString("F0") + " ms",
                node.SpeedUtc >= DateTime.UtcNow.AddDays(-1) && node.Throughput > 0 ? (node.Throughput / 1048576).ToString("F2") + " MiB/s" : "待采样",
                node.LastUtc.ToLocalTime().ToString("MM-dd HH:mm") }));
        }
        memoryPage.Controls.Add(memory);
        memoryPage.Controls.Add(Note("这些长期数据仅供诊断和回顾。后台寻优每轮都会重新实测基础网络、ChatGPT、Gemini 和实际出口地区，不直接用历史分数决定第一名。"));
        tabs.TabPages.Add(memoryPage);
    }
    private static Label Note(string text) { return new Label { Dock = DockStyle.Top, AutoSize = false, Height = 90, Text = text, Padding = new Padding(10) }; }
    private static ListView Table(params string[] columns)
    {
        var view = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, ShowItemToolTips = true };
        foreach (string column in columns) view.Columns.Add(column, column.Contains("节点") || column == "原因" || column.Contains("资格") ? 300 : 125);
        return view;
    }
}
