using System;
using System.Collections.Generic;
using System.Linq;
using Grayson.Vision.Contracts.Templates.Models;
using Grayson.Vision.HalconWrapper.Templates;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.CalibrationLocation.NccMatch
{
    public class NccMatchParam : ParamBase
    {
        /// <summary>调整最小分数等参数时预览窗口实时显示匹配位置十字与分数。</summary>
        public override bool SupportsPreview => true;

        private string _templateName = "";
        /// <summary>
        /// 引用的模板名称（模板管理界面创建，全局唯一）。
        /// 运行时经 TemplateManager 解析加载模板文件（.ncc），替代旧的 ModelId 内存句柄。
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
        /// 刷新模板下拉列表。防丢值：当前已选模板若不在候选列表，补一个占位项，
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
    }
}
