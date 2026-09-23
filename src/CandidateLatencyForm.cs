using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

public sealed class CandidateLatencyForm : Form
{
    private readonly Action requestRefresh;
    private readonly Label statusLabel = new Label();
    private readonly DataGridView latencyTable = new DataGridView();

    public CandidateLatencyForm(MonitorSnapshot snapshot, Action requestRefresh)
    {
        this.requestRefresh = requestRefresh ?? delegate { };
        Text = "候选节点网站延迟排行";
        Icon = AppIcon.Current;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(1100, 520);
        MinimumSize = new Size(780, 420);
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1,
            RowCount = 4, Padding = new Padding(18) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label { Text = "前 10 个候选节点 · 各网站实测延迟", AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold),
            ForeColor = Color.FromArgb(24, 33, 47) };
        layout.Controls.Add(title);
        statusLabel.AutoSize = true;
        statusLabel.ForeColor = Color.FromArgb(88, 101, 122);
        statusLabel.Margin = new Padding(0, 5, 0, 10);
        layout.Controls.Add(statusLabel);

        latencyTable.Name = "candidateLatencyTable";
        latencyTable.Dock = DockStyle.Fill;
        latencyTable.ReadOnly = true;
        latencyTable.AllowUserToAddRows = false;
        latencyTable.AllowUserToDeleteRows = false;
        latencyTable.AllowUserToResizeRows = false;
        latencyTable.MultiSelect = false;
        latencyTable.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        latencyTable.RowHeadersVisible = false;
        latencyTable.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells;
        latencyTable.BackgroundColor = Color.White;
        latencyTable.BorderStyle = BorderStyle.FixedSingle;
        latencyTable.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        layout.Controls.Add(latencyTable);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var refresh = new Button { Text = "重新实测前 10 名", AutoSize = true };
        refresh.Click += delegate {
            statusLabel.Text = "正在测速；完成后表格会自动更新。";
            this.requestRefresh();
        };
        buttons.Controls.Add(refresh);
        buttons.Controls.Add(new Label { Text = "网站探测并发执行；候选节点逐个验证，不影响当前节点选择。",
            AutoSize = true, ForeColor = Color.FromArgb(88, 101, 122), Margin = new Padding(12, 7, 0, 0) });
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        UpdateSnapshot(snapshot);
    }

    public void UpdateSnapshot(MonitorSnapshot snapshot)
    {
        if (snapshot == null || IsDisposed) return;
        IList<CandidateLatencyMeasurement> rows = snapshot.CandidateLatencies ??
            new List<CandidateLatencyMeasurement>().AsReadOnly();
        ServiceKind[] services = rows.SelectMany(x => x.Services).Select(x => x.Service)
            .Distinct().OrderBy(x => x).ToArray();

        latencyTable.SuspendLayout();
        latencyTable.Columns.Clear();
        latencyTable.Rows.Clear();
        AddColumn("rank", "排名", 54, true);
        AddColumn("node", "候选节点", 230, true);
        AddColumn("country", "出口", 60, true);
        AddColumn("mihomo", "Clash 延迟", 88, true);
        foreach (ServiceKind service in services)
            AddColumn("service_" + service, MonitorPresentation.ServiceLabel(service), 90, false);

        for (int index = 0; index < Math.Max(10, rows.Count); index++)
        {
            if (index >= rows.Count)
            {
                int placeholder = latencyTable.Rows.Add();
                latencyTable.Rows[placeholder].Cells["rank"].Value = (index + 1).ToString();
                latencyTable.Rows[placeholder].Cells["node"].Value = "待测";
                continue;
            }
            CandidateLatencyMeasurement row = rows[index];
            int rowIndex = latencyTable.Rows.Add();
            DataGridViewRow gridRow = latencyTable.Rows[rowIndex];
            gridRow.Cells["rank"].Value = (index + 1).ToString();
            gridRow.Cells["node"].Value = row.Node;
            gridRow.Cells["country"].Value = String.IsNullOrWhiteSpace(row.ExitCountryCode) ? "未知" : row.ExitCountryCode;
            gridRow.Cells["mihomo"].Value = row.MihomoMilliseconds > 0 && row.MihomoMilliseconds < Int32.MaxValue
                ? row.MihomoMilliseconds + " ms" : "超时";
            foreach (ServiceMeasurement service in row.Services)
                gridRow.Cells["service_" + service.Service].Value = WebsiteLatencyText(service);
        }
        DateTime checkedUtc = rows.Select(x => x.CheckedUtc).DefaultIfEmpty(DateTime.MinValue).Max();
        statusLabel.Text = rows.Count == 0 ? "尚未实测。点击下方按钮开始测量。" :
            "按各网站响应 P75 排序 · 最近实测：" + checkedUtc.ToLocalTime().ToString("MM-dd HH:mm:ss");
        latencyTable.ResumeLayout();
    }

    private void AddColumn(string name, string title, int minimumWidth, bool frozen)
    {
        var column = new DataGridViewTextBoxColumn { Name = name, HeaderText = title,
            MinimumWidth = minimumWidth, Frozen = frozen, SortMode = DataGridViewColumnSortMode.NotSortable };
        latencyTable.Columns.Add(column);
    }

    private static string WebsiteLatencyText(ServiceMeasurement service)
    {
        if (service == null || service.Evidence == ProbeFailureKind.Unverified) return "待验证";
        if (!service.Available) return "不可用";
        return service.Milliseconds > 0 ? service.Milliseconds + " ms" : "可用";
    }
}
