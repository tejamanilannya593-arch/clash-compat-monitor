using System;
using System.Drawing;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows.Forms;

public sealed class StatisticsForm : Form
{
    public StatisticsForm()
    {
        Text = "运行统计（本次启动）"; Icon = AppIcon.Current;
        Font = new Font("Microsoft YaHei UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        using (var graphics = CreateGraphics()) Size = new Size((int)(650 * graphics.DpiX / 96), (int)(420 * graphics.DpiY / 96));
        var report = RunStatistics.Capture();
        var text = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None };
        text.Text = "版本：" + report.Version + "\r\n本次启动：" + report.StartedUtc.ToLocalTime().ToString("g") +
            "\r\n已完成检测轮次：" + report.CompletedCycles + "\r\n异常轮次：" + report.AttentionCycles +
            "\r\n已确认选路变更：" + report.ConfirmedSelections +
            "\r\n最近 / 最长检测耗时：" + report.LastCycleSeconds.ToString("F1") + " / " + report.MaximumCycleSeconds.ToString("F1") + " 秒" +
            "\r\n当前工作集 / 私有内存：" + report.WorkingSetMiB.ToString("F1") + " / " + report.PrivateMemoryMiB.ToString("F1") + " MiB" +
            "\r\n累计 CPU 时间：" + report.CpuSeconds.ToString("F1") + " 秒" +
            "\r\n已读取探测响应正文：" + (report.ReceivedBodyBytes / 1048576.0).ToString("F2") + " MiB" +
            "\r\n\r\n统计仅限本次启动。工作集受系统回收影响，不能单独代表内存需求。正文统计不含协议开销、Mihomo 延迟探测、重传或未读取缓冲数据，不能当作机场账单流量。异常轮次也不等于断网时长。" +
            "\r\n\r\n导出文件只包含以上数值、版本及时间，不含节点名、订阅、密钥、账号或日志。不会自动上传。";
        var export = new Button { Text = "导出统计 JSON", Dock = DockStyle.Bottom, Height = 44 };
        export.Click += delegate {
            using (var dialog = new SaveFileDialog { Filter = "JSON|*.json", FileName = "monitor-statistics.json" })
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                try { File.WriteAllText(dialog.FileName, new JavaScriptSerializer().Serialize(report)); }
                catch (Exception ex) { if (!(ex is IOException || ex is UnauthorizedAccessException)) throw; MessageBox.Show(this, "保存失败，请选择可写目录。"); }
            }
        };
        Controls.Add(text); Controls.Add(export);
    }
}
