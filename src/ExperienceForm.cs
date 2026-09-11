using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

public sealed class ExperienceForm : Form
{
    public ExperienceForm()
    {
        Text = "切换记录与推荐";
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
        var memoryPage = new TabPage("历史推荐");
        var memory = Table("节点", "寻优资格与进度", "参考分", "严格成功率", "平均响应", "响应波动", "近期采样速度", "最近检测");
        var recommendations = data.Recommend(data.ActiveScope, data.Nodes.Select(x => x.Node), DateTime.UtcNow).ToList();
        var memories = data.Nodes.Where(x => x.Scope == data.ActiveScope && x.LastUtc >= DateTime.UtcNow.AddDays(-7))
            .OrderByDescending(x => x.Rank(DateTime.UtcNow)).ToList();
        foreach (var node in memories)
        {
            double strictRate = node.OutcomeSamples == 0 ? 0 : (double)node.SuccessfulSamples / node.OutcomeSamples;
            bool stable = data.IsProvenStable(data.ActiveScope, node.Node, DateTime.UtcNow);
            bool preferredLatency = QualityPolicy.CandidateLatencyIsPreferred(data.RecentResponses(data.ActiveScope, node.Node, 5));
            string qualification = stable && preferredLatency ? "历史已达标；仍需本轮服务复检" :
                data.StabilitySummary(data.ActiveScope, node.Node, DateTime.UtcNow) + (stable ? " · 延迟分布未达优质" : "");
            memory.Items.Add(new ListViewItem(new[] { node.Node, qualification, node.Rank(DateTime.UtcNow).ToString("F0"),
                strictRate.ToString("P0"), node.ResponseMs.ToString("F0") + " ms", node.JitterMs.ToString("F0") + " ms",
                node.SpeedUtc >= DateTime.UtcNow.AddDays(-1) && node.Throughput > 0 ? (node.Throughput / 1048576).ToString("F2") + " MiB/s" : "待采样",
                node.LastUtc.ToLocalTime().ToString("MM-dd HH:mm") }));
        }
        memoryPage.Controls.Add(memory);
        memoryPage.Controls.Add(Note((recommendations.Count == 0 ? "界面推荐尚在积累；性能寻优采用更严格的 5 次、30 分钟和 95% 成功率门槛。\r\n" : "按稳定性、响应、抖动和近期实测速率排序，推荐节点仍需实时复检。\r\n") +
            "候选最近 5 次中位响应需不超过 500 ms、P95 不超过 800 ms、波动不超过 150 ms；任一服务本轮超过 1000 ms 都不会用于性能切换。"));
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
