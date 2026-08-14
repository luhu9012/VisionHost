# Grayson.Vision.HalconWrapper

Halcon 算子封装层，面向公司视觉框架提供统一、安全的图像处理 API。本层是系统中唯一允许直接引用 `HalconDotNet` 的项目。上层 `Nodes`、插件及其他业务模块禁止直接使用 Halcon 原生对象。

## 1. 基础信息

- **目标框架**：.NET Framework 4.7.2 x64
- **语言**：C# 7.x
- **适配 Halcon 版本**：19.11 x64
- **必须引用**：
  - `Grayson.Vision.Contracts`
  - `Grayson.Vision.Common`
  - `HalconDotNet.dll`（复制到输出目录：如果较新则复制）

## 2. 模块与目录结构

```
Grayson.Vision.HalconWrapper
├─ Core
│  ├─ HalconMemoryGuard.cs      HObject 内存托管，防止泄露
│  └─ HalconGlobalHelper.cs     Pose 转换、像素/毫米换算、坐标系绘制
├─ ImageProc
│  ├─ ImageBasicTool.cs         图像读写、灰度转换、ROI 裁剪
│  ├─ ImageFilterTool.cs        高斯/中值滤波、形态学运算
│  ├─ ImageThresholdTool.cs     固定阈值、动态阈值、Otsu、区域面积筛选
│  ├─ AffineImageTool.cs        图像仿射变换（旋转/平移）
│  ├─ RoiOperationTool.cs       Region 转 Mask、ROI 集合运算
│  └─ ImagePreprocessTool.cs    面向 UI/节点的 object 类型统一预处理入口
├─ Match2D
│  └─ TemplateMatchTool.cs      shape_model 创建、查找、释放
├─ Measure2D
│  └─ EdgeMeasureTool.cs        卡尺测量（边缘、圆孔，预留接口）
└─ Calibration
   └─ Calib2DTool.cs            九点标定、像素↔世界坐标换算
```

## 3. 设计原则

1. **唯一 Halcon 入口**：只有本层可直接 `using HalconDotNet`，上层通过 `Result<T>` 与本层交互。
2. **统一返回值**：所有公开方法均返回 `Result<T>` 或 `Result`，调用方无需处理 Halcon 原生异常。
3. **内存安全**：`HalconMemoryGuard` 批量托管临时 `HObject`，`using` 结束自动释放。
4. **异常隔离**：底层异常统一转 `Result.Fail`，日志记录后上层只拿到业务错误信息，不暴露 Halcon 细节。

## 4. 快速使用

### 4.1 加载并显示结果

```csharp
var result = ImageBasicTool.ReadImageFile("D:/sample.bmp");
if (!result.Success)
{
	// 处理失败：result.Message
	return;
}
HObject img = result.Data;
// 使用 img，使用完毕后由调用方负责释放或继续托管给 Guard
```

### 4.2 图像预处理链

```csharp
using (var guard = new HalconMemoryGuard())
{
	var gray = ImageBasicTool.RgbToGray(img);
	if (!gray.Success) return;

	var blur = ImageFilterTool.GaussFilter(gray.Data, 5);
	if (!blur.Success) return;

	var region = ImageThresholdTool.FixedThreshold(blur.Data, 0, 128);
	if (!region.Success) return;

	guard.Register(gray.Data);
	guard.Register(blur.Data);
	// region.Data 若为下游输出结果，可单独管理
}
```

### 4.3 模板匹配

```csharp
// 1. 创建模板
var createModel = TemplateMatchTool.CreateShapeModel(
	templateImg, roiRow1, roiCol1, roiRow2, roiCol2,
	angleStart: -20, angleEnd: 20);

if (!createModel.Success) return;
int modelId = createModel.Data;

// 2. 在线匹配
var find = TemplateMatchTool.FindShapeModel(searchImg, modelId, 0.7);
if (find.Success && find.Data.Length > 0)
{
	var match = find.Data[0];
	Console.WriteLine($"Row={match.PixelRow}, Col={match.PixelCol}, Score={match.Score}");
}

// 3. 释放模板
TemplateMatchTool.ClearShapeModel(modelId);
```

### 4.4 九点标定与坐标转换

```csharp
// 计算标定矩阵
var calibResult = Calib2DTool.CalcNinePointHomMat(
	pixelXList, pixelYList,
	worldXList, worldYList);

if (!calibResult.Success) return;
HTuple homMat = calibResult.Data;

// 像素 -> 世界 mm
var world = HalconGlobalHelper.PixelToWorldMm(pixelX, pixelY, homMat);

// 保存/读取
Calib2DTool.SaveHomMatToFile(homMat, "calib.tup");
Calib2DTool.LoadHomMatFromFile("calib.tup");
```

## 5. 各模块 API 一览

### 5.1 Core

| 文件 | 关键类型 | 说明 |
|------|----------|------|
| `HalconMemoryGuard` | `Register`、`CleanAll`、`Dispose` | 批量托管 `HObject` 生命周期 |
| `HalconGlobalHelper` | `PixelToWorldMm`、`WorldMmToPixel`、`DrawCross`、`Pose3D↔HTuple` | 坐标换算、绘图、Pose 转换 |

### 5.2 ImageProc

| 文件 | 主要能力 |
|------|----------|
| `ImageBasicTool` | `ReadImageFile`、`SaveImageToFile`、`RgbToGray`、`CropImage` |
| `ImageFilterTool` | `GaussFilter`、`MedianFilter`、`Erode`、`Dilate`、`OpenMorph`、`CloseMorph` |
| `ImageThresholdTool` | `FixedThreshold`、`AutoThreshold`、`OtsuThreshold`、`SelectRegionByArea` |
| `AffineImageTool` | `RotateImage` |
| `RoiOperationTool` | `RegionToMask`、`CombineRegions`（1=交集/2=并集/3=差集） |
| `ImagePreprocessTool` | `ApplyFilter`、`ApplyThreshold`、`ApplyRoiCrop`、`ApplyCombineRegions`、`ApplyAffineRotate`（object 形态入口） |

### 5.3 Match2D

| 文件 | 主要能力 |
|------|----------|
| `TemplateMatchTool` | `CreateShapeModel`、`FindShapeModel`、`ClearShapeModel` |
| `TemplateMatchResult` | `PixelRow`、`PixelCol`、`RotateDegree`、`Score` |

### 5.4 Measure2D

| 文件 | 主要能力 |
|------|----------|
| `EdgeMeasureTool` | `MeasureLineDistance`、`MeasureCircleDiameter`（当前为骨架，后续按需实现） |

### 5.5 Calibration

| 文件 | 主要能力 |
|------|----------|
| `Calib2DTool` | `CalcNinePointHomMat`、`SaveHomMatToFile`、`LoadHomMatFromFile` |

## 6. 使用规范

- 对临时 `HObject` 优先使用 `using (var guard = new HalconMemoryGuard())` 托管，避免单个 `Dispose` 散落。
- 返回给上层的图像对象建议使用 `Result<T>` 传递，生命周期由接收方负责。
- 禁止在本层之外创建新的 `HalconDotNet` 依赖，保证后续 Halcon 版本升级只改动本层。
- `.NET Framework 4.7.2` 下不支持部分高版本 C# 语法，保持 C# 7.x 兼容。

## 7. 更新记录

- 更新目录结构以匹配真实源码文件（新增 `AffineImageTool`、`RoiOperationTool`、`ImagePreprocessTool`）。
- 调整基础配置说明为 `.NET Framework 4.7.2 x64`。
- 补充使用示例与 API 一览表，减少 README 中直接内嵌完整源码的比重。
