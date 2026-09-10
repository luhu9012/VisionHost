// Grayson.Vision.WpfUI/View/NewTaskTemplateWindow.xaml.cs
// 新建任务模板向导（2026-09-09 stage9-2）：
// 行业全量任务类型目录 → 已实现的三大任务族条目可生成模板（进入 TaskTemplateEditWindow 二次编辑），
// 规划中条目仅展示禁用。示例落地（DL Demo 翻译）入口在模板中心工具栏，不混入本目录。
using Grayson.Vision.Contracts.TaskLibrary.Models;
using Grayson.Vision.WpfUI.Service;
using Grayson.Vision.WpfUI.ViewModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace Grayson.Vision.WpfUI.View
{
    public partial class NewTaskTemplateWindow : Window
    {
        public IndustryTaskType SelectedCatalogItem => CatalogGrid.SelectedItem as IndustryTaskType;

        public NewTaskTemplateWindow()
        {
            InitializeComponent();
            CatalogGrid.ItemsSource = TaskTypeIndustryCatalog.All
                .OrderByDescending(i => i.IsImplemented)
                .ThenBy(i => i.Id)
                .ToList();
            HintText.Text = "已实现族 3 类（引导定位 / 深度学习推理 / 外观测量）共 8 项；行业全量目录共 " + TaskTypeIndustryCatalog.All.Count + " 项，其余为规划中展示项。";
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            TryGenerateSelected();
        }

        private void CatalogGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            TryGenerateSelected();
        }

        /// <summary>生成选中条目的任务模板草稿并打开编辑器；保存成功即关向导。</summary>
        private void TryGenerateSelected()
        {
            var item = SelectedCatalogItem;
            if (item == null)
            {
                MessageBox.Show(this, "请先在目录中选择一个任务类型。", "提示");
                return;
            }
            if (!item.IsImplemented || !item.FamilyKind.HasValue)
            {
                MessageBox.Show(this, $"「{item.Name}」为行业全量中的规划条目，暂未实现。\n当前可生成的只有 引导定位 / 深度学习推理 / 外观测量 三大任务族。",
                    "规划中", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var draft = BuildDraft(item.FamilyKind.Value, item.DefaultTemplateName);
            var vm = new TaskTemplateEditViewModel(draft, isNew: true);
            var editor = new TaskTemplateEditWindow { Owner = this };
            editor.DataContext = vm;
            if (editor.ShowDialog() == true)
            {
                DialogResult = true; // 向导任务完成，关闭并让中心页刷新
            }
        }

        /// <summary>按族构造默认草稿（引导定位默认挂 VisionPickPlace 引擎；DL/测量默认独立纯软件 + 本地文件夹源）。</summary>
        private static TaskTemplateInfo BuildDraft(TaskKind kind, string defaultName)
        {
            var library = new TaskTemplateLibraryService();
            var tpl = new TaskTemplateInfo
            {
                TemplateCode = library.GenerateCode(kind),
                DisplayName = string.IsNullOrWhiteSpace(defaultName)
                    ? TaskKindCatalog.KindDisplay(kind) + "模板"
                    : defaultName,
                Kind = kind,
                DependencyMode = TaskKindCatalog.DefaultDependency(kind),
                Status = TaskTemplateStatus.Draft,
                Version = "v1",
                ImageSource = kind == TaskKind.PositioningGuidance
                    ? new TaskTemplateImageSource { Kind = TaskImageSourceKind.CameraSource, CameraSlotKey = "TopCam" }
                    : new TaskTemplateImageSource { Kind = TaskImageSourceKind.LocalFolder }
            };
            // 引导定位：默认翻译通用旋转取放引擎（新机型零代码建档首选）；编辑器内可改
            if (kind == TaskKind.PositioningGuidance)
            {
                tpl.EngineKey = "VisionPickPlace";
                tpl.VerdictToIo = true;
                tpl.OutputContractText = "视觉定位 → 坐标输出 → 执行方案引擎按节拍完成取放（配方链承载视觉段，示教面板配置节拍参数）。";
            }
            else if (kind == TaskKind.DeepLearningInference)
            {
                tpl.OutputContractText = "深度学习推理输出 目标框/掩膜/类别/异常 结果摘要（DetectionResults），判据随模板 VerdictRule 执行，周期结果写 CSV。";
            }
            else
            {
                tpl.OutputContractText = "外观测量输出 数值 + OK/NG 判定（测量项在编辑页配置，判据随模板执行）。";
            }
            return tpl;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
