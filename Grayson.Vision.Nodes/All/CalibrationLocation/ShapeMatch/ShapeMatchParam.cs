using System;
using System.Collections.Generic;
using System.Linq;
using Grayson.Vision.Contracts.Templates.Models;
using Grayson.Vision.HalconWrapper.Templates;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.CalibrationLocation.ShapeMatch
{
    public class ShapeMatchParam : ParamBase
    {
        /// <summary>调整最小分数等参数时预览窗口实时显示匹配位置十字与分数。</summary>
        public override bool SupportsPreview => true;

        private string _templateName = "";
        /// <summary>
        /// 引用的模板名称（模板管理界面创建，全局唯一）。
        /// 运行时经 TemplateManager 解析加载模板文件（.shm），替代旧的 ModelId 内存句柄。
        /// ⚠ setter 过滤 null：ComboBox.SelectedValue 双向绑定在 ItemsSource 重建瞬间
        /// 会把当前值写成 null（经典 WPF 坑），忽略之可保证已选模板不丢。
        /// </summary>
        public string TemplateName
        {
            get => _templateName;
            set
            {
                if (value == null) return;
                Set(ref _templateName, value);
            }
        }

        private List<TemplateInfo> _templateOptions = new List<TemplateInfo>();
        /// <summary>模板管理界面提供的可选模板（属性面板下拉数据源，打开面板时 RefreshTemplates 刷新）</summary>
        public List<TemplateInfo> TemplateOptions
        {
            get => _templateOptions;
            set => Set(ref _templateOptions, value);
        }

        /// <summary>
        /// 刷新模板下拉列表（每次打开属性面板调用，保证能引用到新建的模板）。
        /// 防丢值：当前已选模板若不在候选列表（如模板被删、大小写不同），补一个占位项，
        /// 保证下拉框仍显示当前值、配方里引用的 TemplateName 不被清空。
        /// </summary>
        public void RefreshTemplates()
        {
            string prev = _templateName;
            var res = new TemplateManager().GetAll();
            var list = res.Success ? res.Data : new List<TemplateInfo>();
            if (!string.IsNullOrEmpty(prev) &&
                list.All(t => !string.Equals(t.Name, prev, StringComparison.OrdinalIgnoreCase)))
            {
                list.Insert(0, new TemplateInfo { Name = prev });
            }
            TemplateOptions = list;
        }

        private double _minScore = 0.7;
        public double MinScore
        {
            get => _minScore;
            set => Set(ref _minScore, value);
        }

        // 默认全角度搜索：配合模板训练时的 -180~180，节点新建时直接覆盖任意旋转。
        // 旧配方仍使用其序列化值；如需提速可在此收窄范围（必须在模板训练范围内）。
        private double _angleStart = -180.0;
        /// <summary>搜索起始角度（°），运行时传给 find_shape_model；需在模板训练角度范围内</summary>
        public double AngleStart
        {
            get => _angleStart;
            set => Set(ref _angleStart, value);
        }

        private double _angleEnd = 180.0;
        /// <summary>搜索终止角度（°），运行时传给 find_shape_model；需在模板训练角度范围内</summary>
        public double AngleEnd
        {
            get => _angleEnd;
            set => Set(ref _angleEnd, value);
        }

        // ── 搜索区域（Search ROI）：与模板 ROI（学习区域）分离 ──
        // 模板 ROI = 学什么（模板管理界面框选）；搜索区域 = 在哪里找（本节点限定）。
        // 视野大/背景干扰多时限定搜索区域可显著提速并降低误检；为空 = 全图搜索。
        private bool _searchRoiEnabled;
        /// <summary>是否启用搜索区域限定（false = 全图搜索）</summary>
        public bool SearchRoiEnabled
        {
            get => _searchRoiEnabled;
            set => Set(ref _searchRoiEnabled, value);
        }

        private double _searchRow1;
        /// <summary>搜索区域左上角行（图像 Y 坐标）</summary>
        public double SearchRow1 { get => _searchRow1; set => Set(ref _searchRow1, value); }

        private double _searchCol1;
        /// <summary>搜索区域左上角列（图像 X 坐标）</summary>
        public double SearchCol1 { get => _searchCol1; set => Set(ref _searchCol1, value); }

        private double _searchRow2;
        /// <summary>搜索区域右下角行</summary>
        public double SearchRow2 { get => _searchRow2; set => Set(ref _searchRow2, value); }

        private double _searchCol2;
        /// <summary>搜索区域右下角列</summary>
        public double SearchCol2 { get => _searchCol2; set => Set(ref _searchCol2, value); }

        /// <summary>搜索区域是否有效（启用且坐标合法）</summary>
        public bool HasValidSearchRoi =>
            _searchRoiEnabled && _searchRow2 > _searchRow1 && _searchCol2 > _searchCol1;
    }
}
