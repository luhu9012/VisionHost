using System.Text;
using System.Windows;
using System.Windows.Input;
using Grayson.Vision.Contracts.Calibration.Chain;

namespace Grayson.Vision.WpfUI.View
{
    /// <summary>
    /// 链校验台（范式2 L1/L2）。只读审计：载入 Chain.json → ChainAuditor →
    /// 节点行（形状/条件数/像素当量/CalibZ/DeltaRef）+ 全局门禁结果 + 边清单。
    /// 与向导/生产共享同一把尺（ChainEngine.Validate + ChainFitter 门限），自身零判据。
    /// </summary>
    public partial class ChainVerifyWindow : Window
    {
        private ChainAuditReport _report;

        public ChainVerifyWindow(string stationCode = null)
        {
            InitializeComponent();
            StationBox.Text = string.IsNullOrWhiteSpace(stationCode) ? "ST_002" : stationCode;
            RunAudit();
        }

        private void Audit_Click(object sender, RoutedEventArgs e) { RunAudit(); }

        private void StationBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) RunAudit();
        }

        private void RunAudit()
        {
            string code = StationBox.Text.Trim();
            if (code.Length == 0) return;
            _report = ChainAuditor.AuditStation(code);

            RowGrid.ItemsSource = _report.Rows;
            EdgeText.Text = _report.Loaded
                ? "边（形状=消费入口宣告）：" + string.Join("；", _report.EdgeLines)
                : "";

            var sb = new StringBuilder();
            if (!_report.Loaded)
            {
                sb.AppendLine(_report.LoadError);
            }
            else
            {
                foreach (var issue in _report.GlobalIssues) sb.AppendLine("【全局】" + issue);
                foreach (var row in _report.Rows)
                {
                    foreach (var info in row.Infos) sb.AppendLine("[" + row.Target + "] " + info);
                }
                if (_report.AllOk) sb.AppendLine("✓ 全部通过：链已就绪，生产端可 fail-closed 装载。");
            }
            DetailBox.Text = sb.ToString();

            VerdictText.Text = !_report.Loaded ? "✗ 未落盘（fail-closed 待补链）"
                : (_report.AllOk ? "✓ 审计通过" : "✗ 审计有问题（见问题列）");
        }
    }
}
