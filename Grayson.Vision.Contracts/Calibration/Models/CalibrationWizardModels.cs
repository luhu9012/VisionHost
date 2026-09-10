//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationWizardModels.cs
// 说 明: 标定向导 v2 步骤引擎领域模型（2026-09-05 重设计 P1a）。
//        原则（拍板③）：向导不再有"通用四步壳"——步骤即数据：
//        CalibrationStepDef 描述单步（标题/引导/面板键/自动采集/前置条件），
//        CalibrationWizardTemplates.BuildFor(spec) 按任务规格(物理量×采集路径)
//        装配步骤序列；VM 只渲染"当前步骤"，Next 就绪态由步骤角色解释。
//        面板键 PanelKey 是 UI 面板注册表索引（P1b VM 按键切面板，替代旧
//        DataTemplateSelector 按 CalibrationType 分支的 5 模板堆叠）。
//===================================================================================
using System.Collections.Generic;

namespace Grayson.Vision.Contracts.Calibration.Models
{
    /// <summary>
    /// 向导步骤角色（全局复用 8 角色；任务模板按需装配子集）。
    /// 每个角色在 VM 侧有独立的"完成判定"解释器（采集点数/基准已设/拟合已跑…）。
    /// </summary>
    public enum WizardStepRole
    {
        /// <summary>绑定相机/运动卡/轴号（从档案 ActuatorKind 预填，Epson 槽位约定自动）</summary>
        BindDevices = 0,

        /// <summary>设参考位/基准（EyeInHand 走位=网格基准位；吸放式=吸取位+拍照位）</summary>
        DefineOrigin = 1,

        /// <summary>选择并验证特征提取（圆 Mark / 十字 / 模板匹配）</summary>
        DefineFeature = 2,

        /// <summary>网格打点采集（H 走位 / H 吸放 / s 标距）</summary>
        SampleGrid = 3,

        /// <summary>旋转采样（e：吸件转 U 多角度 / 相机观测旋转）</summary>
        SampleRotate = 4,

        /// <summary>对针（t 专属：步进→锁 M_tool→抓拍→图像点选→写 ToolOffset）</summary>
        AlignTool = 5,

        /// <summary>拟合计算 + 几何自检（残差/方向/尺度；不过不给下一步）</summary>
        ComputeFit = 6,

        /// <summary>内嵌验收（残差体检 + 可选打点抽验）→ 发布门禁</summary>
        VerifyAndPublish = 7
    }

    /// <summary>
    /// 单个向导步骤定义（模板装配产物；静态数据，无 UI 引用）。
    /// </summary>
    public class CalibrationStepDef
    {
        public CalibrationStepDef() { }

        public CalibrationStepDef(WizardStepRole role, string title, string guide,
            string panelKey = null, bool autoRunSupported = false, string precondition = null)
        {
            Role = role;
            Title = title;
            GuideText = guide;
            PanelKey = string.IsNullOrWhiteSpace(panelKey) ? Role.ToString().ToLowerInvariant() : panelKey;
            AutoRunSupported = autoRunSupported;
            PreconditionText = precondition;
        }

        /// <summary>步骤角色（VM 完成判定解释键）</summary>
        public WizardStepRole Role { get; set; }

        /// <summary>步骤标题（胶囊/页头显示）</summary>
        public string Title { get; set; }

        /// <summary>引导文案（当前步主提示）</summary>
        public string GuideText { get; set; }

        /// <summary>UI 面板注册键（P1b VM 面板字典索引）</summary>
        public string PanelKey { get; set; }

        /// <summary>该步支持"一键自动采集"？（走位网格/旋转采样支持；对针/拟合不支持）</summary>
        public bool AutoRunSupported { get; set; }

        /// <summary>前置条件人读文案（缺前置时按钮态禁用提示，如"依赖 H(ST_003|Cam_A) 已发布"）</summary>
        public string PreconditionText { get; set; }

        /// <summary>面板键格式化（角色名小写），供快速构造</summary>
        public static string DefaultPanelKey(WizardStepRole role)
        {
            return role.ToString().ToLowerInvariant();
        }
    }
}
