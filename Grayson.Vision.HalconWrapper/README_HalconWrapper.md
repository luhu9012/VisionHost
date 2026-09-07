# Grayson.Vision.HalconWrapper — HALCON 业务化封装（视觉内核）

> 校正：2026-09-07。系统内**唯一允许直接引用 HalconDotNet 的工程之一**（另一个是 HalconWrapper.Wpf）。上层（Nodes/插件/UI）禁止直接 new 原生 HALCON 对象。

## 1. 目录结构

| 目录 | 职责 |
|---|---|
| `Templates\` | 模板学习/保存/加载/源图留档（CreateTemplate 在学习帧落 `Config\Templates\Sources\{模板名}.png` 并回填 `SourceImagePath`） |
| `Match2D\` | 形状匹配（ShapeModel）/ 相关匹配（NCC） |
| `Measure2D\` | 卡尺 / 边缘 / 亚像素测量（测量辅助类） |
| `Calibration\` | 九点标定 / 手眼求解 / 旋转中心 / 像素当量 |
| `ImageProc\` | 通用图像处理（滤波/阈值/形态学/Blob） |
| `Identification\` | 识别类（条码/OCR 等） |
| `Core\` | 封装基类/图像上下文 |
| `NodePreviewHelper.cs` | 节点运行期预览辅助 |

依赖：仅 `Contracts`；无 WPF（WPF 显示在 `HalconWrapper.Wpf`）。

## 2. 算子与句柄铁律（新封装前必读）

1. **模型句柄一律 HTuple(HHandle) 原样传**，不做 IntPtr 中间转换；
2. `GenImageInterleaved` = **12 参**（bits=-1, shift=0）；`MinMaxGray` = **6 参**；
3. **不存在的算子**（幻觉高发区，新增前跑 `.workbuddy/verify_halcon_operators.py` dnfile 静态核对）：
   - ❌ `GenRectangle1ContourXld` → 用 `GenContourPolygonXld`
   - ❌ `GenRegionBoundary` → 用 `GenContourRegionXld`
   - ❌ `HomMat2dToAffinePara`、`FindCircle` 等（以 halcondotnet.dll 实测为准）
4. `ImageOverlay` 的属性名是 **Column**（非 Col/行）；
5. `set_shape_model_origin` = 相对域重心偏移——固化 origin 后旧 `.shm/.ncc` 必须重建；
6. **算子绝不跑 UI 线程**；HImage 借用语义（见根 ARCHITECTURE §6）。

## 3. 模板工程要点（工艺级约束）

- ROI：目标占 ≥50%、框心=吸取点；NumLevels/Contrast auto、MinContrast 10；自测分数≈1.0 属正常；
- 光照梯度大 → `EnableFlatField=true` 后**重建模板**；
- 源图留档：`Sources\{模板名}.png` 用于"编辑选中"回读原图改 ROI/掩膜；同名覆盖天然复用同文件；Delete 只清 Sources 留档、**用户原图绝不删**；
- 存量空 `SourceImagePath` 老模板：原帧已失，需现场同名覆盖重学一次自动补档。

## 4. 扩展指南

```text
在对应目录建 XxxService/Helper → 走 HalconDotNet → 返回 HObject/HTuple 结果
→ 上层(Nodes)只依赖本工程公开类，不直接碰原生句柄
```

## 5. 关联文档

- 根 `ARCHITECTURE.md` §4.4/§6；`视觉交互翻译层设计方案_v1_2026-09-05.md`（显示侧）
- 算子核查脚本 `.workbuddy/verify_halcon_operators.py`（PYTHONPATH=envs/default/Lib/site-packages）
