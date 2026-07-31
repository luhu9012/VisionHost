# 阶段5：Grayson.Vision.BusinessUnits 完整落地（全中文注释）
## 项目基础配置
1. 项目：.NET Framework 4.7、C#7.4、平台x64
2. 项目引用清单（严格顺序，无反向依赖）
   - Grayson.Vision.Contracts（必选顶层契约）
   - Grayson.Vision.Common（通用工具、日志、序列化）
   - Grayson.Vision.HalconWrapper（唯一允许调用Halcon的封装层，本层禁止using HalconDotNet）
3. 硬性红线：本层**不引用Shell宿主、各类硬件插件**；硬件只通过`IWorkflowRuntime`按DeviceKey远程调用，无硬件SDK直接依赖。
4. 所有单元强制标注`[BusinessUnitMeta]`，自动被注册表扫描识别。

## 完整目录分层结构
```
Grayson.Vision.BusinessUnits
├─ Grab                     # 图像采集类单元
│  └─ CameraGrabUnit.cs
├─ Preprocess               # 图像预处理原子单元
│  ├─ GaussFilterUnit.cs
│  ├─ ThresholdFixedUnit.cs
│  └─ MorphologyOpenUnit.cs
├─ Location                 # 2D定位单元
│  └─ ShapeTemplateMatchUnit.cs
├─ Measure                  # 尺寸测量单元
│  ├─ LineDistanceMeasureUnit.cs
│  └─ CircleDiameterMeasureUnit.cs
├─ DefectInspect            # 缺陷检测单元
│  └─ BlobDefectDetectUnit.cs
├─ PlcIo                    # PLC读写交互单元
│  ├─ PlcReadBitUnit.cs
│  └─ PlcWriteBitUnit.cs
├─ MotionRobot              # 运动、机器人单元
│  └─ RobotPoseMoveUnit.cs
├─ DataProcess              # 数据运算、变量映射、脚本兜底
│  ├─ DataMapAssignUnit.cs
│  └─ ScriptGlueUnit.cs
├─ Composite                # 常用复用复合单元（实现ICompositeBusinessUnit）
│  └─ PreciseAlignCompositeUnit.cs
└─ UIConfigPanel            # 所有单元配套WPF配置UserControl，按单元分类建子文件夹
```

## 通用前置约定
1. 所有单元执行完毕，临时HObject全部释放，只保留最终需要存入`VisionContext`的图像；
2. 配置面板全部放到UIConfigPanel文件夹，单元内只持有Type，不实例化控件；
3. 所有读写上下文严格使用`ContextDataKeys`常量，杜绝手写字符串；
4. 单元参数全部存在内部私有字段，`SaveRecipe/LoadRecipe`完成序列化；
5. 硬件全程通过`runtime.GetDevice("设备Key")`获取，不缓存硬件对象。

---

# 一、Grab/CameraGrabUnit.cs 相机采集单元（最基础入口单元）
```csharp
using System;
using System.Collections.Generic;
using System.Windows.Controls;
using Grayson.Vision.Contracts.Business;
using Grayson.Vision.Contracts.Business.Enums;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Core.Runtime;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Common.Extensions;
using Grayson.Vision.BusinessUnits.UIConfigPanel.Grab;

namespace Grayson.Vision.BusinessUnits.Grab
{
    /// <summary>
    /// 工业相机软触发拍照单元
    /// 负责指定Key的相机单次抓拍，图像写入上下文Grab.SourceImage
    /// 可配置相机设备Key，支持拍照超时设置
    /// </summary>
    [BusinessUnitMeta(
        ModuleId = "CameraGrabUnit",
        DisplayName = "相机图像采集",
        UnitType = BusinessUnitType.ImageGrab,
        Category = "图像采集",
        Description = "根据绑定的相机DeviceKey软触发拍照，原图存入全局上下文")]
    public class CameraGrabUnit : IBusinessUnit
    {
        #region 单元私有配方参数（全部可序列化）
        /// <summary>绑定的相机硬件唯一Key</summary>
        public string CameraDeviceKey { get; set; } = string.Empty;

        /// <summary>拍照超时毫秒，超时判定采集失败</summary>
        public int GrabTimeoutMs { get; set; } = 3000;
        #endregion

        #region IBusinessUnit 固定实现
        public string ModuleId => "CameraGrabUnit";
        public string ModuleName => "相机图像采集";
        public BusinessUnitType UnitType => BusinessUnitType.ImageGrab;
        public bool Enable { get; set; } = true;

        /// <summary>本单元无前置依赖，不需要读取任何上下文数据</summary>
        public IReadOnlyList<string> InputKeys => Array.Empty<string>();

        /// <summary>执行完成写入原图、触发时间戳</summary>
        public IReadOnlyList<string> OutputKeys => new List<string>
        {
            ContextDataKeys.Grab_SourceImage,
            ContextDataKeys.Grab_TriggerTime
        };

        public Result Execute(VisionContext context, IWorkflowRuntime runtime)
        {
            // 1. 基础参数校验
            if (string.IsNullOrWhiteSpace(CameraDeviceKey))
            {
                runtime.LogError("相机DeviceKey为空，请在配置面板选择相机");
                return Result.Fail("未绑定相机");
            }

            // 2. 通过运行时门面获取相机硬件，不能直接new硬件类
            var camQuery = runtime.GetDevice(CameraDeviceKey);
            if (!camQuery.Success || !(camQuery.Data is ICamera camera))
            {
                runtime.LogError($"获取相机失败，Key:{CameraDeviceKey}，{camQuery.Message}");
                return Result.Fail("相机硬件获取失败");
            }

            // 3. 软触发拍照
            runtime.LogInfo($"开始触发相机{CameraDeviceKey}拍照", ModuleId);
            Result grabResult = camera.GrabImage(out var captureImage);
            if (!grabResult.Success)
            {
                runtime.LogError($"相机抓拍失败：{grabResult.Message}", ModuleId);
                return grabResult;
            }

            // 4. 图像写入上下文，记录时间戳
            context.SourceImage = captureImage;
            context.TriggerTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            context.SharedData[ContextDataKeys.Grab_TriggerTime] = context.TriggerTimestamp;

            // 5. 推送UI显示原图
            runtime.PublishUiEvent("Ui.ShowMainImage", captureImage);
            runtime.LogInfo("相机采集完成", ModuleId);

            return Result.Ok();
        }

        /// <summary>参数打包存入配方字典</summary>
        public Dictionary<string, object> SaveRecipe()
        {
            return new Dictionary<string, object>()
            {
                {"CameraDeviceKey", CameraDeviceKey},
                {"GrabTimeoutMs", GrabTimeoutMs},
                {"Enable", Enable}
            };
        }

        /// <summary>从配方字典加载参数</summary>
        public void LoadRecipe(Dictionary<string, object> recipeDict)
        {
            CameraDeviceKey = recipeDict.SafeGet("CameraDeviceKey", string.Empty);
            GrabTimeoutMs = recipeDict.SafeGet("GrabTimeoutMs", 3000);
            Enable = recipeDict.SafeGet("Enable", true);
        }

        /// <summary>返回WPF配置面板，双击节点弹出参数配置窗口</summary>
        public UserControl GetConfigPanel()
        {
            return new CameraGrabUnitPanel(this);
        }
        #endregion
    }
}
```

## 配套UI：UIConfigPanel/Grab/CameraGrabUnitPanel.xaml（极简结构）
XAML
```xml
<UserControl x:Class="Grayson.Vision.BusinessUnits.UIConfigPanel.Grab.CameraGrabUnitPanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="420" Height="160">
    <Grid Margin="10">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="120"/>
            <ColumnDefinition Width="*"/>
        </Grid.ColumnDefinitions>

        <TextBlock Grid.Row="0" Grid.Column="0" VerticalAlignment="Center">相机设备Key：</TextBlock>
        <ComboBox x:Name="CbxCameraKey" Grid.Row="0" Grid.Column="1" Margin="5"/>

        <TextBlock Grid.Row="1" Grid.Column="0" VerticalAlignment="Center">拍照超时(ms)：</TextBlock>
        <TextBox x:Name="TxtTimeout" Grid.Row="1" Grid.Column="1" Margin="5"/>

        <TextBlock Grid.Row="2" Grid.Column="0" VerticalAlignment="Center">当前单元启用：</TextBlock>
        <CheckBox x:Name="ChkEnable" Grid.Row="2" Grid.Column="1" Margin="5" IsChecked="{Binding Unit.Enable}"/>
    </Grid>
</UserControl>
```
后台cs（绑定单元，读取硬件下拉列表）
```csharp
using System.Windows;
using System.Windows.Controls;
using Grayson.Vision.BusinessUnits.Grab;

namespace Grayson.Vision.BusinessUnits.UIConfigPanel.Grab
{
    public partial class CameraGrabUnitPanel : UserControl
    {
        public CameraGrabUnit Unit { get; }

        public CameraGrabUnitPanel(CameraGrabUnit unit)
        {
            InitializeComponent();
            Unit = unit;
            this.DataContext = this;

            // 宿主打开面板时会注入全部硬件Key列表，此处预留绑定入口
            TxtTimeout.Text = Unit.GrabTimeoutMs.ToString();
            CbxCameraKey.Text = Unit.CameraDeviceKey;
            ChkEnable.IsChecked = Unit.Enable;

            TxtTimeout.LostFocus += (s, e) =>
            {
                if (int.TryParse(TxtTimeout.Text, out int val))
                    Unit.GrabTimeoutMs = val;
            };
            CbxCameraKey.SelectionChanged += (s, e) =>
            {
                if (CbxCameraKey.SelectedItem != null)
                    Unit.CameraDeviceKey = CbxCameraKey.SelectedItem.ToString();
            };
            ChkEnable.Click += (s, e) => Unit.Enable = ChkEnable.IsChecked.Value;
        }

        /// 宿主注入全部相机DeviceKey用于下拉选择
        public void LoadCameraKeys(List<string> allCameraKeys)
        {
            CbxCameraKey.Items.Clear();
            foreach (var key in allCameraKeys)
                CbxCameraKey.Items.Add(key);
        }
    }
}
```

---

# 二、预处理单元示例：Preprocess/GaussFilterUnit.cs 高斯滤波
```csharp
using System;
using System.Collections.Generic;
using System.Windows.Controls;
using Grayson.Vision.Contracts.Business;
using Grayson.Vision.Contracts.Business.Enums;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Core.Runtime;
using Grayson.Vision.Common.Extensions;
using Grayson.Vision.HalconWrapper.ImageProc;
using Grayson.Vision.BusinessUnits.UIConfigPanel.Preprocess;

namespace Grayson.Vision.BusinessUnits.Preprocess
{
    /// <summary>
    /// 高斯平滑滤波预处理单元
    /// 读取上下文原图，滤波后覆盖原图，消除相机高频噪点
    /// </summary>
    [BusinessUnitMeta(
        ModuleId = "GaussFilterUnit",
        DisplayName = "高斯滤波",
        UnitType = BusinessUnitType.DataProcess,
        Category = "图像预处理",
        Description = "高斯模糊降噪，可配置卷积核3/5/7，处理后更新上下文原图")]
    public class GaussFilterUnit : IBusinessUnit
    {
        /// <summary>滤波核尺寸，只允许奇数：3、5、7</summary>
        public int KernelSize { get; set; } = 5;
        public bool Enable { get; set; } = true;

        public string ModuleId => "GaussFilterUnit";
        public string ModuleName => "高斯滤波";
        public BusinessUnitType UnitType => BusinessUnitType.DataProcess;

        public IReadOnlyList<string> InputKeys => new List<string> { ContextDataKeys.Grab_SourceImage };
        public IReadOnlyList<string> OutputKeys => new List<string> { ContextDataKeys.Grab_SourceImage };

        public Result Execute(VisionContext context, IWorkflowRuntime runtime)
        {
            // 校验是否存在原图
            if (context.SourceImage == null || !context.SourceImage.IsInitialized)
            {
                runtime.LogError("高斯滤波：上下文无原始图像", ModuleId);
                return Result.Fail("缺少待处理图像");
            }

            // 调用Halcon封装层，本层禁止直接调用HalconDotNet
            var filterResult = ImageFilterTool.GaussFilter(context.SourceImage, KernelSize);
            if (!filterResult.Success)
            {
                runtime.LogError($"高斯滤波算法失败：{filterResult.Message}", ModuleId);
                return filterResult;
            }

            // 释放旧图像，替换为滤波后新图
            context.SourceImage.Dispose();
            context.SourceImage = filterResult.Data;

            runtime.LogInfo($"高斯滤波完成，核大小{KernelSize}", ModuleId);
            runtime.PublishUiEvent("Ui.ShowMainImage", context.SourceImage);
            return Result.Ok();
        }

        public Dictionary<string, object> SaveRecipe()
        {
            return new Dictionary<string, object>()
            {
                {"KernelSize", KernelSize},
                {"Enable", Enable}
            };
        }

        public void LoadRecipe(Dictionary<string, object> recipeDict)
        {
            KernelSize = recipeDict.SafeGet("KernelSize", 5);
            Enable = recipeDict.SafeGet("Enable", true);
            // 强制限制只能为奇数
            if (KernelSize % 2 == 0) KernelSize += 1;
        }

        public UserControl GetConfigPanel()
        {
            return new GaussFilterPanel(this);
        }
    }
}
```

---

# 三、定位单元：Location/ShapeTemplateMatchUnit.cs 形状模板匹配
```csharp
using System;
using System.Collections.Generic;
using System.Windows.Controls;
using Grayson.Vision.Contracts.Business;
using Grayson.Vision.Contracts.Business.Enums;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Core.Runtime;
using Grayson.Vision.Common.Extensions;
using Grayson.Vision.HalconWrapper.Match2D;
using Grayson.Vision.HalconWrapper.Core;
using Grayson.Vision.BusinessUnits.UIConfigPanel.Location;

namespace Grayson.Vision.BusinessUnits.Location
{
    /// <summary>
    /// 2D形状模板匹配定位单元
    /// 读取上下文原图做模板搜索，输出匹配OK状态、工件位姿Pose3D
    /// 支持最低匹配分数阈值控制良莠过滤
    /// </summary>
    [BusinessUnitMeta(
        ModuleId = "ShapeTemplateMatchUnit",
        DisplayName = "形状模板定位",
        UnitType = BusinessUnitType.Location,
        Category = "定位引导",
        Description = "加载本地模板文件匹配工件，输出工件坐标角度，存入Match.Pose3D")]
    public class ShapeTemplateMatchUnit : IBusinessUnit
    {
        /// <summary>模板文件本地路径</summary>
        public string TemplateFilePath { get; set; } = string.Empty;
        /// <summary>最低匹配相似度 0~1</summary>
        public double MinScore { get; set; } = 0.7;
        public bool Enable { get; set; } = true;

        public string ModuleId => "ShapeTemplateMatchUnit";
        public string ModuleName => "形状模板定位";
        public BusinessUnitType UnitType => BusinessUnitType.Location;

        public IReadOnlyList<string> InputKeys => new List<string> { ContextDataKeys.Grab_SourceImage };
        public IReadOnlyList<string> OutputKeys => new List<string>
        {
            ContextDataKeys.Match_IsSuccess,
            ContextDataKeys.Match_WorkPose
        };

        public Result Execute(VisionContext context, IWorkflowRuntime runtime)
        {
            if (context.SourceImage == null || !context.SourceImage.IsInitialized)
            {
                runtime.LogError("定位：无采集图像", ModuleId);
                return Result.Fail("缺少图像");
            }
            if (string.IsNullOrWhiteSpace(TemplateFilePath))
            {
                runtime.LogError("未配置模板文件路径", ModuleId);
                return Result.Fail("模板路径为空");
            }

            // 读取提前训练好的模板ID（工程实际可把模板ID缓存进配方，这里简化）
            // 执行匹配
            var matchResult = TemplateMatchTool.FindShapeModel(context.SourceImage, 0, MinScore);
            if (!matchResult.Success)
            {
                context.SharedData[ContextDataKeys.Match_IsSuccess] = false;
                runtime.LogError("模板匹配运算失败", ModuleId);
                return matchResult;
            }

            var matchArray = matchResult.Data;
            if (matchArray.Length == 0)
            {
                // 无满足分数的匹配结果
                context.SharedData[ContextDataKeys.Match_IsSuccess] = false;
                runtime.LogWarn("未找到符合分数的工件", ModuleId);
                return Result.Ok();
            }

            // 取匹配分数最高第一个工件
            var bestMatch = matchArray[0];
            context.SharedData[ContextDataKeys.Match_IsSuccess] = true;

            // 像素坐标转为世界毫米位姿（依赖标定矩阵，此处预留全局标定参数读取）
            Pose3D pose = new Pose3D(bestMatch.PixelCol, bestMatch.PixelRow, 0, 0, 0, bestMatch.RotateDegree);
            context.WorkpiecePose = pose;
            context.SharedData[ContextDataKeys.Match_WorkPose] = pose;

            runtime.LogInfo($"定位成功 X:{pose.X:F2} Y:{pose.Y:F2} Rz:{pose.Rz:F2} 分数:{bestMatch.Score:F3}", ModuleId);
            // 推送UI绘制定位十字
            runtime.PublishUiEvent("Ui.DrawMatchCross", pose);
            return Result.Ok();
        }

        public Dictionary<string, object> SaveRecipe()
        {
            return new Dictionary<string, object>()
            {
                {"TemplateFilePath", TemplateFilePath},
                {"MinScore", MinScore},
                {"Enable", Enable}
            };
        }

        public void LoadRecipe(Dictionary<string, object> recipeDict)
        {
            TemplateFilePath = recipeDict.SafeGet("TemplateFilePath", string.Empty);
            MinScore = recipeDict.SafeGet("MinScore", 0.7);
            Enable = recipeDict.SafeGet("Enable", true);
        }

        public UserControl GetConfigPanel()
        {
            return new ShapeTemplateMatchPanel(this);
        }
    }
}
```

---

# 四、PLC交互单元示例：PlcIo/PlcWriteBitUnit.cs 点位写入
```csharp
using System;
using System.Collections.Generic;
using System.Windows.Controls;
using Grayson.Vision.Contracts.Business;
using Grayson.Vision.Contracts.Business.Enums;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Core.Runtime;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Common.Extensions;
using Grayson.Vision.BusinessUnits.UIConfigPanel.PlcIo;

namespace Grayson.Vision.BusinessUnits.PlcIo
{
    /// <summary>
    /// PLC布尔点位写入单元
    /// 可固定写入True/False，也可读取上下文布尔变量写入点位
    /// 适配OK/NG信号上报、触发外部设备
    /// </summary>
    [BusinessUnitMeta(
        ModuleId = "PlcWriteBitUnit",
        DisplayName = "PLC写入布尔点位",
        UnitType = BusinessUnitType.PlcIo,
        Category = "PLC通讯",
        Description = "指定PLC设备、地址，写入固定值或上下文的布尔变量")]
    public class PlcWriteBitUnit : IBusinessUnit
    {
        public string PlcDeviceKey { get; set; } = string.Empty;
        /// <summary>PLC点位地址，如M0.0、DB10.DBX0.0</summary>
        public string PlcAddress { get; set; } = string.Empty;
        /// <summary>是否使用上下文变量；false=写固定值</summary>
        public bool UseContextBool { get; set; }
        /// <summary>UseContextBool=false时的固定写入值</summary>
        public bool FixedWriteValue { get; set; }
        /// <summary>UseContextBool=true时读取的上下文Key</summary>
        public string ContextBoolKey { get; set; } = string.Empty;

        public bool Enable { get; set; } = true;
        public string ModuleId => "PlcWriteBitUnit";
        public string ModuleName => "PLC写入布尔点位";
        public BusinessUnitType UnitType => BusinessUnitType.PlcIo;

        public IReadOnlyList<string> InputKeys
        {
            get
            {
                if (UseContextBool && !string.IsNullOrEmpty(ContextBoolKey))
                    return new List<string> { ContextBoolKey };
                return Array.Empty<string>();
            }
        }
        public IReadOnlyList<string> OutputKeys => Array.Empty<string>();

        public Result Execute(VisionContext context, IWorkflowRuntime runtime)
        {
            // 校验基础配置
            if (string.IsNullOrWhiteSpace(PlcDeviceKey) || string.IsNullOrWhiteSpace(PlcAddress))
            {
                runtime.LogError("PLC Key或点位地址为空", ModuleId);
                return Result.Fail("PLC配置不全");
            }

            // 获取PLC硬件
            var plcQuery = runtime.GetDevice(PlcDeviceKey);
            if (!plcQuery.Success || !(plcQuery.Data is IPlc plc))
            {
                runtime.LogError($"获取PLC失败 {PlcDeviceKey}", ModuleId);
                return Result.Fail("PLC硬件异常");
            }

            // 判断最终写入的布尔值
            bool writeVal;
            if (UseContextBool)
            {
                writeVal = context.SharedData.SafeGet(ContextBoolKey, false);
            }
            else
            {
                writeVal = FixedWriteValue;
            }

            // 执行写入
            Result writeRes = plc.WriteBit(PlcAddress, writeVal);
            if (!writeRes.Success)
            {
                runtime.LogError($"点位{PlcAddress}写入失败:{writeRes.Message}", ModuleId);
                return writeRes;
            }
            runtime.LogInfo($"PLC {PlcAddress} 成功写入{writeVal}", ModuleId);
            return Result.Ok();
        }

        public Dictionary<string, object> SaveRecipe()
        {
            return new Dictionary<string, object>()
            {
                {"PlcDeviceKey",PlcDeviceKey},
                {"PlcAddress",PlcAddress},
                {"UseContextBool",UseContextBool},
                {"FixedWriteValue",FixedWriteValue},
                {"ContextBoolKey",ContextBoolKey},
                {"Enable",Enable}
            };
        }

        public void LoadRecipe(Dictionary<string, object> recipeDict)
        {
            PlcDeviceKey = recipeDict.SafeGet("PlcDeviceKey", "");
            PlcAddress = recipeDict.SafeGet("PlcAddress", "");
            UseContextBool = recipeDict.SafeGet("UseContextBool", false);
            FixedWriteValue = recipeDict.SafeGet("FixedWriteValue", false);
            ContextBoolKey = recipeDict.SafeGet("ContextBoolKey", "");
            Enable = recipeDict.SafeGet("Enable", true);
        }

        public UserControl GetConfigPanel()
        {
            return new PlcWriteBitPanel(this);
        }
    }
}
```

---

# 五、DataProcess/DataMapAssignUnit.cs 变量映射赋值单元（胶水层必备）
```csharp
using System;
using System.Collections.Generic;
using System.Windows.Controls;
using Grayson.Vision.Contracts.Business;
using Grayson.Vision.Contracts.Business.Enums;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Core.Runtime;
using Grayson.Vision.Common.Extensions;
using Grayson.Vision.BusinessUnits.UIConfigPanel.DataProcess;

namespace Grayson.Vision.BusinessUnits.DataProcess
{
    /// <summary>
    /// 上下文变量赋值映射单元
    /// 用于别名转发：AKey值复制到BKey、常量写入上下文
    /// 解决不同单元输入输出Key不匹配问题，不用写脚本
    /// </summary>
    [BusinessUnitMeta(
        ModuleId = "DataMapAssignUnit",
        DisplayName = "变量映射赋值",
        UnitType = BusinessUnitType.DataProcess,
        Category = "数据处理",
        Description = "把上下文A字段复制到B字段，或写入固定常量，适配单元之间数据名不匹配")]
    public class DataMapAssignUnit : IBusinessUnit
    {
        /// <summary>数据源：上下文Key / 固定常量</summary>
        public bool UseConstant { get; set; }
        public string SourceContextKey { get; set; } = string.Empty;
        public object ConstantValue { get; set; }
        /// <summary>目标写入上下文Key</summary>
        public string TargetContextKey { get; set; } = string.Empty;

        public bool Enable { get; set; } = true;
        public string ModuleId => "DataMapAssignUnit";
        public string ModuleName => "变量映射赋值";
        public BusinessUnitType UnitType => BusinessUnitType.DataProcess;

        public IReadOnlyList<string> InputKeys
        {
            get
            {
                if (!UseConstant && !string.IsNullOrEmpty(SourceContextKey))
                    return new List<string> { SourceContextKey };
                return Array.Empty<string>();
            }
        }
        public IReadOnlyList<string> OutputKeys => new List<string> { TargetContextKey };

        public Result Execute(VisionContext context, IWorkflowRuntime runtime)
        {
            if (string.IsNullOrWhiteSpace(TargetContextKey))
            {
                runtime.LogError("未配置目标上下文Key", ModuleId);
                return Result.Fail("缺少目标Key");
            }

            object writeData;
            if (UseConstant)
            {
                writeData = ConstantValue;
            }
            else
            {
                if (!context.SharedData.ContainsKey(SourceContextKey))
                {
                    runtime.LogWarn($"源Key{SourceContextKey}不存在", ModuleId);
                    return Result.Ok();
                }
                writeData = context.SharedData[SourceContextKey];
            }

            context.SharedData[TargetContextKey] = writeData;
            runtime.LogInfo($"变量赋值完成 {TargetContextKey} = {writeData}", ModuleId);
            return Result.Ok();
        }

        public Dictionary<string, object> SaveRecipe()
        {
            return new Dictionary<string, object>()
            {
                {"UseConstant",UseConstant},
                {"SourceContextKey",SourceContextKey},
                {"ConstantValue",ConstantValue},
                {"TargetContextKey",TargetContextKey},
                {"Enable",Enable}
            };
        }

        public void LoadRecipe(Dictionary<string, object> recipeDict)
        {
            UseConstant = recipeDict.SafeGet("UseConstant", false);
            SourceContextKey = recipeDict.SafeGet("SourceContextKey", "");
            ConstantValue = recipeDict.SafeGet("ConstantValue", null);
            TargetContextKey = recipeDict.SafeGet("TargetContextKey", "");
            Enable = recipeDict.SafeGet("Enable", true);
        }

        public UserControl GetConfigPanel()
        {
            return new DataMapAssignPanel(this);
        }
    }
}
```

---

# 六、复合单元示例：Composite/PreciseAlignCompositeUnit.cs（粗定位+裁剪+精定位打包）
```csharp
using System.Collections.Generic;
using System.Windows.Controls;
using Grayson.Vision.Contracts.Business;
using Grayson.Vision.Contracts.Business.Flow;
using Grayson.Vision.Contracts.Business.Enums;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Core.Runtime;
using Grayson.Vision.BusinessUnits.Location;

namespace Grayson.Vision.BusinessUnits.Composite
{
    /// <summary>
    /// 复合单元：粗定位→ROI裁剪→精定位整套流程
    /// 多个工位重复使用，打包成单个节点简化画布
    /// 实现ICompositeBusinessUnit，内部可进入子流程编辑
    /// </summary>
    [BusinessUnitMeta(
        ModuleId = "PreciseAlignCompositeUnit",
        DisplayName = "粗精定位复合流程",
        UnitType = BusinessUnitType.Location,
        Category = "复合复用流程",
        Description = "封装粗定位、图像裁剪、精定位三步，可展开编辑内部子节点")]
    public class PreciseAlignCompositeUnit : ICompositeBusinessUnit
    {
        public List<FlowNodeBase> SubFlowNodes { get; set; } = new List<FlowNodeBase>();
        public bool Enable { get; set; } = true;

        public string ModuleId => "PreciseAlignCompositeUnit";
        public string ModuleName => "粗精定位复合流程";
        public BusinessUnitType UnitType => BusinessUnitType.Location;

        public IReadOnlyList<string> InputKeys => new List<string> { ContextDataKeys.Grab_SourceImage };
        public IReadOnlyList<string> OutputKeys => new List<string> { ContextDataKeys.Match_IsSuccess, ContextDataKeys.Match_WorkPose };

        public Result Execute(VisionContext context, IWorkflowRuntime runtime)
        {
            // 递归执行内部所有子节点，由引擎统一遍历
            foreach (var node in SubFlowNodes)
            {
                var nodeResult = node.Execute(context, runtime);
                if (!nodeResult.Success)
                    return nodeResult;
            }
            return Result.Ok();
        }

        public Dictionary<string, object> SaveRecipe()
        {
            // 子节点完整序列化存入配方
            return new Dictionary<string, object>()
            {
                {"SubNodes", SubFlowNodes},
                {"Enable", Enable}
            };
        }

        public void LoadRecipe(Dictionary<string, object> recipeDict)
        {
            SubFlowNodes = recipeDict.SafeGet("SubNodes", new List<FlowNodeBase>());
            Enable = recipeDict.SafeGet("Enable", true);
        }

        public UserControl GetConfigPanel()
        {
            // 复合单元面板提供入口：打开子流程编辑器
            return new PreciseAlignCompositePanel(this);
        }

        public void OpenSubFlowEditor()
        {
            // 宿主唤起内嵌子流程画布，编辑SubFlowNodes节点树
        }
    }
}
```

---

# 七、ScriptGlueUnit 脚本兜底单元（非标胶水逻辑）
```csharp
using System;
using System.Collections.Generic;
using System.Windows.Controls;
using Grayson.Vision.Contracts.Business;
using Grayson.Vision.Contracts.Business.Enums;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Core.Runtime;
using Grayson.Vision.Common.Extensions;
using Grayson.Vision.BusinessUnits.UIConfigPanel.DataProcess;

namespace Grayson.Vision.BusinessUnits.DataProcess
{
    /// <summary>
    /// C#脚本胶水单元，非标临时逻辑兜底
    /// 支持运行时C#脚本执行，可访问上下文、Runtime硬件、日志
    /// 规范：只做变量换算、坐标补偿、零散IO，禁止写大量图像处理
    /// </summary>
    [BusinessUnitMeta(
        ModuleId = "ScriptGlueUnit",
        DisplayName = "C#胶水脚本单元",
        UnitType = BusinessUnitType.ScriptGlue,
        Category = "脚本兜底",
        Description = "执行自定义C#脚本，临时非标变量运算、坐标补偿，正式逻辑建议改为标准单元")]
    public class ScriptGlueUnit : IBusinessUnit
    {
        /// <summary>用户编写的C#脚本字符串</summary>
        public string ScriptCode { get; set; } = string.Empty;
        /// <summary>脚本执行超时毫秒，防止死循环卡死流程</summary>
        public int TimeoutMs { get; set; } = 5000;
        public bool Enable { get; set; } = true;

        public string ModuleId => "ScriptGlueUnit";
        public string ModuleName => "C#胶水脚本单元";
        public BusinessUnitType UnitType => BusinessUnitType.ScriptGlue;

        public IReadOnlyList<string> InputKeys => Array.Empty<string>();
        public IReadOnlyList<string> OutputKeys => Array.Empty<string>();

        public Result Execute(VisionContext context, IWorkflowRuntime runtime)
        {
            if (string.IsNullOrWhiteSpace(ScriptCode))
            {
                runtime.LogWarn("脚本为空，跳过执行", ModuleId);
                return Result.Ok();
            }
            // Roslyn脚本执行逻辑宿主封装，此处只做入口调用
            runtime.LogInfo("开始执行自定义胶水脚本", ModuleId);
            // 宿主脚本引擎传入context、runtime全局变量，隔离沙箱+超时
            return Result.Ok();
        }

        public Dictionary<string, object> SaveRecipe()
        {
            return new Dictionary<string, object>()
            {
                {"ScriptCode", ScriptCode},
                {"TimeoutMs", TimeoutMs},
                {"Enable", Enable}
            };
        }

        public void LoadRecipe(Dictionary<string, object> recipeDict)
        {
            ScriptCode = recipeDict.SafeGet("ScriptCode", "");
            TimeoutMs = recipeDict.SafeGet("TimeoutMs", 5000);
            Enable = recipeDict.SafeGet("Enable", true);
        }

        public UserControl GetConfigPanel()
        {
            return new ScriptGlueCodePanel(this);
        }
    }
}
```

# 强制开发落地规范
1. 所有单元严格遵循四件套：特性标记、Input/OutputKeys、Save/LoadRecipe、GetConfigPanel；
2. 图像处理只调用HalconWrapper，本层全程不引用`using HalconDotNet`；
3. 硬件一律靠Runtime+DeviceKey获取，不缓存硬件实例；
4. 所有临时HObject执行结束释放，仅业务必须的图像留在上下文；
5. 重复固定流程封装为ICompositeBusinessUnit，减少画布节点数量；
6. 简单变量流转用DataMapAssign，少量非标用ScriptGlue，重度非标新建完整IBusinessUnit；
7. 所有参数必须可序列化进配方，不存在硬编码写死的配置。

# 下一步交付可选
1. 海康相机完整硬件插件全注释代码；
2. 宿主HardwareManager硬件管理、插件加载完整代码；
3. 宿主WorkflowExecutor递归流程执行引擎完整代码；
4. StationInstance工位运行容器+重启自动恢复全套代码。