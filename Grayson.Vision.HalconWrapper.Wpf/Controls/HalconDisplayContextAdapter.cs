//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: HalconImageDisplayHost 的 ICalibrationDisplayContext 适配器（场景式 API）。
//        控件本身不实现 ICalibrationDisplayContext —— 接口成员携带 halondotnet 类型
//        （HObject），若出现在控件公共 API 上，XAML 编译器解析控件类型时必须加载
//        halcondotnet，触发 MC1000 依赖解析错误（本机缺 PresentationCore 3.0）。
//        适配器只在代码后台 new（绝不进入任何 XAML），由它持有控件引用并转发到
//        控件的 internal 场景方法，再以 ICalibrationDisplayContext 身份注入
//        ViewModel → CalibrationService。
//===================================================================================

using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.HalconWrapper.Calibration;
using Grayson.Vision.HalconWrapper.Core;
using HalconDotNet;
using System;

namespace Grayson.Vision.HalconWrapper.Wpf.Controls
{
    /// <summary>
    /// HalconImageDisplayHost 的显示上下文适配器。
    /// 仅在代码后台创建（不进入 XAML），避免控件公共 API 暴露 halcondotnet 类型。
    /// 同时实现三个接口：
    ///   - ICalibrationDisplayContext（HalconWrapper，强类型 HObject）：标定服务用；
    ///   - IFlowPreviewContext（Contracts，弱类型 object）：节点属性面板实时预览用，
    ///     弱类型入口让 Contracts/Nodes 保持零 halcondotnet 引用；
    ///   - IHalconWindowSurface（HalconWrapper，HDevelop 风格直通）：把原生 HWindow 交给
    ///     算法层自由绘制，绘制内容由调用方书写，不再受限于场景式 API 的固定方法集。
    ///     三块能力共用同一个实例，算法层用 AsWindow() 判型取用。
    /// </summary>
    public sealed class HalconDisplayContextAdapter : ICalibrationDisplayContext, IFlowPreviewContext, IHalconWindowSurface
    {
        private readonly HalconImageDisplayHost _host;

        /// <summary>
        /// 适配器当前绑定的宿主控件。
        /// 标定向导有多个显示控件（步骤2特征配置页一个、第三步各标定类型模板各一个），
        /// 切页后由向导窗口比对 Host 决定是否把适配器重指到当前可见控件。
        /// </summary>
        public HalconImageDisplayHost Host => _host;

        /// <summary>
        /// 创建适配器，绑定宿主控件。
        /// </summary>
        /// <param name="host">Halcon 视图宿主控件（拥有 HWindow 句柄）</param>
        public HalconDisplayContextAdapter(HalconImageDisplayHost host)
        {
            _host = host ?? throw new System.ArgumentNullException(nameof(host));
        }

        /// <inheritdoc />
        public bool IsReady => _host.IsReady;

        /// <inheritdoc />
        public void BeginScene()
        {
            _host.SceneBegin();
        }

        /// <inheritdoc />
        public void Add(HObject obj, string color = null, int lineWidth = 1)
        {
            _host.SceneAddObject(obj, color, lineWidth);
        }

        /// <inheritdoc />
        public void AddBorrowed(HObject obj)
        {
            _host.SceneAddBorrowed(obj);
        }

        /// <inheritdoc />
        public void AddText(string text, double row, double col, string color)
        {
            _host.SceneAddText(text, row, col, color);
        }

        /// <inheritdoc />
        public void AddCross(double row, double col, double size, string color)
        {
            _host.SceneAddCross(row, col, size, color);
        }

        /// <inheritdoc />
        public void AddCircle(double row, double col, double radius, string color)
        {
            _host.SceneAddCircle(row, col, radius, color);
        }

        #region IHalconWindowSurface（HDevelop 风格直通：把 HWindow 交给算法层自由绘制）

        /// <inheritdoc />
        public void Draw(Action<HWindow> draw)
        {
            _host.RunOnWindow(draw);
        }

        /// <inheritdoc />
        public void DrawRecorded(Action<HWindow> draw)
        {
            _host.SceneAddAction(draw);
        }

        #endregion

        #region IFlowPreviewContext（弱类型显式实现：object 判型转发到同一套场景 API）

        bool IFlowPreviewContext.IsReady => IsReady;

        void IFlowPreviewContext.BeginScene() => BeginScene();

        void IFlowPreviewContext.Add(object obj, string color, int lineWidth)
        {
            // 非 HObject（含 null）静默忽略：弱类型入口无法静态约束，防御性判型
            if (obj is HObject hObj)
            {
                _host.SceneAddObject(hObj, color, lineWidth);
            }
        }

        void IFlowPreviewContext.AddBorrowed(object obj)
        {
            if (obj is HObject hObj)
            {
                _host.SceneAddBorrowed(hObj);
            }
        }

        void IFlowPreviewContext.AddText(string text, double row, double col, string color)
            => AddText(text, row, col, color);

        void IFlowPreviewContext.AddCross(double row, double col, double size, string color)
            => AddCross(row, col, size, color);

        void IFlowPreviewContext.AddCircle(double row, double col, double radius, string color)
            => AddCircle(row, col, radius, color);

        #endregion
    }
}
