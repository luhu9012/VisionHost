using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Contracts.Templates.Models;
using Grayson.Vision.HalconWrapper;
using Grayson.Vision.HalconWrapper.ImageProc;
using Grayson.Vision.HalconWrapper.Match2D;
using Grayson.Vision.HalconWrapper.Templates;
using Grayson.Vision.Nodes.Common;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.All.CalibrationLocation.ShapeMatch
{
    [Node(NodeType.ShapeMatch, NodeCategory.CalibrationLocation, typeof(ShapeMatchParam))]
    [NodePort("InputImage", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#9B59B6")]
    // 端口配色约定：X/列=蓝(#3498DB)、Y/行=橙(#E67E22)、角度=青、分数=绿。
    // 与 CalibrationApply.InputX/InputY 同色配对，连线时一眼可辨 X/Y 是否接反（此前 MatchRow/MatchCol 同为红色导致接反难发现）。
    [NodePort("MatchRow", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#E67E22")]
    [NodePort("MatchCol", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#3498DB")]
    [NodePort("MatchAngle", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#1ABC9C")]
    [NodePort("MatchScore", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#2ECC71")]
    // 匹配效果图输出：端口名含 "Image" → StationWorker.Engine_OnNodeExecuted 自动推送 OnFrameRendered，
    // 编辑器缩略图列表/主视图 与 工位监视视图窗口 双侧自动上屏（与相机采集节点同一通路）。
    // 值 = 输入图同一实例（借用语义）：Display() 的 sameFrame 判定命中，场景叠加层
    // （十字/分数文本/贴合轮廓）随帧重放不被 ClearScene 清掉，主视图呈现完整匹配效果图。
    [NodePort("MatchImage", PortType.Out, PortCategory.Data, dataType: "object", colorHex: "#9B59B6")]
    public class ShapeMatchExecutor : NodeExecutorBase<ShapeMatchParam>
    {
        public const string PORT_IN_IMAGE = "InputImage";
        public const string PORT_OUT_ROW = "MatchRow";
        public const string PORT_OUT_COL = "MatchCol";
        public const string PORT_OUT_ANGLE = "MatchAngle";
        public const string PORT_OUT_SCORE = "MatchScore";
        public const string PORT_OUT_IMAGE = "MatchImage";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, ShapeMatchParam param, NodeExecutionContext context, CancellationToken token)
        {
            // 主动**让出当前同步上下文 (SynchronizationContext)**，把线程使用权交还调度器，让队列里其他等待任务先跑一轮，之后再回来继续执行你后面的代码。
            await Task.Yield();
            // `Stopwatch` 是.NET 高精度计时器。
            // `StartNew()` = 创建实例 + 立刻启动计时，等价于**把前面所有阻塞、排队的工作全部清掉，再开始计时**
            //保证你测量的耗时，**不包含前面积压任务的开销 * *。
            var sw = System.Diagnostics.Stopwatch.StartNew();

            object inputImage = context.GetInputValue<object>(node, PORT_IN_IMAGE);
            if (inputImage == null)
            {
                // 单步（StepNode）只执行本节点，上游采集节点没跑过 → 输入端口没有图像产出。
                // 这是调试期「节点没效果」的高频原因：不是预览没接上，而是根本没有输入可处理。
                context.Log("[" + node.DisplayName + "] 错误：未获取到有效输入图像句柄！" +
                    " 单步执行不会自动运行上游采集节点——请先在工具栏点『相机快照/单次采集』把图送进链路，" +
                    "再单步本节点；若已采图仍报此错，检查上游采集节点的输出端口是否连线到本节点输入。");
                Preview?.BeginScene();
                Preview?.AddText("⚠️ 输入图像为空：单步模式请先点『相机快照』采集一张", 12, 12, "red");
                return;
            }

            if (string.IsNullOrEmpty(param.TemplateName))
            {
                context.Log("[" + node.DisplayName + "] 错误：未选择模板！请在属性面板下拉选择模板管理界面创建的模板。");
                Preview?.BeginScene();
                Preview?.AddBorrowed(inputImage);
                Preview?.AddText("⚠️ 未选择模板，请在属性面板选择", 12, 12, "red");
                return;
            }

            // 诊断日志①：输入图像信息（尺寸/通道数）。匹配算子要求单通道灰度图，
            // 若此处显示"3 通道彩色"说明输入为彩色图，工具层会自动转灰度（见 HalconWrapper 日志）。
            var imgInfo = ImageBasicTool.GetImageInfo(inputImage);
            context.Log($"[{node.DisplayName}] 输入图像: {(imgInfo.Success ? imgInfo.Data : imgInfo.Message)}");

            var templateMgr = new TemplateManager();

            // 诊断日志②：模板元信息（文件路径/ROI/角度范围），0 分排查时确认用的是哪个模板、ROI 框在哪
            var infoRes = templateMgr.GetByName(param.TemplateName);
            if (infoRes.Success)
            {
                var t = infoRes.Data;
                context.Log($"[{node.DisplayName}] 模板: [{t.Name}] 类型={t.Type} 文件={t.ModelFilePath} " +
                    $"ROI=({t.RoiRow1:F0},{t.RoiCol1:F0})-({t.RoiRow2:F0},{t.RoiCol2:F0}) 角度范围={t.AngleStart:F1}°~{t.AngleEnd:F1}° MinScore={param.MinScore:F2}");

                // 诊断日志②-b：模板自测质量 + ROI 尺寸体检。
                // ROI 远大于目标本体时，模型学到的主体是背景（工台纹理）而非目标，
                // 典型症状是「无论目标挪到哪，输出的 Row/Col/Angle 都几乎不变」——
                // 也就是"只有模板点位能成功，换个位置就失败"。这里直接点名，避免现场瞎猜。
                double roiH = System.Math.Abs(t.RoiRow2 - t.RoiRow1);
                double roiW = System.Math.Abs(t.RoiCol2 - t.RoiCol1);
                if (t.QualityScore > 0 && t.QualityScore < 0.7)
                {
                    context.Log($"[{node.DisplayName}] ⚠ 模板 [{t.Name}] 自测分仅 {t.QualityScore:F3}（< 0.7），特征不可靠：" +
                        $"ROI 尺寸 {roiW:F0}×{roiH:F0}px。若目标本体远小于此 ROI，模型学的是背景，" +
                        $"匹配结果会锁死在模板原位姿——请重建模板，让目标本体占 ROI 面积 50% 以上、中心对齐。");
                }
            }
            else
            {
                context.Log($"[{node.DisplayName}] ⚠ 模板元信息读取失败: {infoRes.Message}（匹配继续执行，若模板文件缺失会在此失败）");
            }

            // 预览：输入图为底图
            Preview?.BeginScene();
            Preview?.AddBorrowed(inputImage);

            // 匹配效果图输出（与输入图同一实例，借用语义）：节点执行完由 Engine_OnNodeExecuted
            // 推送帧事件 → 缩略图列表新增/更新 + 主视图显示；同实例命中 Display() sameFrame
            // 判定，上面的场景叠加层（十字/文本/轮廓）与帧共存，主视图即完整匹配效果图。
            // 匹配失败同样推送：底图+红色提示文本保留在场景里，调试时直观可见失败原因。
            context.SetOutputValue(node, PORT_OUT_IMAGE, inputImage);

            // 运行时经 v2 唯一口径 MatchWithDatum 完成"加载句柄→搜索区裁剪→匹配→卡尺精测→释放"全流程
            // （句柄类型封装在 HalconWrapper 层，不泄漏到 Nodes）。输出即 datum（基准点）：模板带卡尺且
            // CircleCenter/LineIntersection 时=卡尺亚像素精测覆盖；无卡尺/Point=锚点直出——与标定采样同口径。
            // 角度范围按节点参数透传；节点参数 Search ROI（可选，限定"在哪里找"）与模板内建搜索框逐级求交（引擎内完成）。
            var outRes = templateMgr.MatchWithDatum(param.TemplateName, inputImage, param.MinScore,
                param.AngleStart, param.AngleEnd,
                null, null, null,
                param.HasValidSearchRoi ? param.SearchRow1 : (double?)null,
                param.HasValidSearchRoi ? param.SearchCol1 : (double?)null,
                param.HasValidSearchRoi ? param.SearchRow2 : (double?)null,
                param.HasValidSearchRoi ? param.SearchCol2 : (double?)null);
            sw.Stop();

            if (outRes.Success && outRes.Data != null && outRes.Data.Match != null)
            {
                var mo = outRes.Data;
                var best = mo.Match;
                // 输出 datum（消费端特征点）：端口 MatchRow/MatchCol 语义=基准点行/列（v2 单一口径）
                context.SetOutputValue(node, PORT_OUT_ROW, mo.DatumRow);
                context.SetOutputValue(node, PORT_OUT_COL, mo.DatumCol);
                context.SetOutputValue(node, PORT_OUT_ANGLE, best.RotateDegree);
                context.SetOutputValue(node, PORT_OUT_SCORE, best.Score);

                // 诊断日志③：候选明细——全部候选逐一列出，单步调试时可观察次优候选的分数梯度
                var sb = new System.Text.StringBuilder();
                var cands = mo.Candidates;
                for (int i = 0; i < cands.Count; i++)
                {
                    var r = cands[i];
                    sb.Append($"[{i + 1}] Row={r.PixelRow:F2} Col={r.PixelCol:F2} Ang={r.RotateDegree:F2}° Score={r.Score:F3}  ");
                }
                context.Log($"[{node.DisplayName}] ✅ 模板匹配成功，候选 {cands.Count} 个，耗时 {sw.ElapsedMilliseconds} ms");
                context.Log($"[{node.DisplayName}]    锚点: Row={best.PixelRow:F2} Col={best.PixelCol:F2} Ang={best.RotateDegree:F2}° Score={best.Score:F3}" +
                    (mo.RefinedByCaliper
                        ? $"  基准点(datum)=({mo.DatumRow:F2},{mo.DatumCol:F2}) [{mo.RefineKind} 卡尺亚像素精测]"
                        : "  基准点=锚点直出"));
                context.Log($"[{node.DisplayName}]    候选明细: {sb}");

                // 诊断日志③-b：基准点(datum)相对视野中心的位置——与「匹配是否真的跟着目标走」配合看
                if (ImageBasicTool.TryGetImageSize(inputImage, out int imgW, out int imgH))
                {
                    double cRow = imgH / 2.0, cCol = imgW / 2.0;
                    double dRow = mo.DatumRow - cRow, dCol = mo.DatumCol - cCol;
                    context.Log($"[{node.DisplayName}]    视野中心: (row {cRow:F1}, col {cCol:F1}) @ {imgW}x{imgH}；" +
                        $"基准点偏离: {dRow:+0.0;-0.0} 行 {dCol:+0.0;-0.0} 列" +
                        $"（离中心 {System.Math.Sqrt(dRow * dRow + dCol * dCol):F0} px）");
                }

                // 卡尺精测边缘点（lime，≤200 防重）——模板带卡尺且本次取样成功时绘制
                int shown = 0;
                if (mo.Measurements != null)
                {
                    foreach (var m in mo.Measurements)
                    {
                        if (m == null || m.Points == null) continue;
                        foreach (var p in m.Points)
                        {
                            if (shown++ >= 200) break;
                            Preview?.AddCross(p.Row, p.Col, 5, "lime");
                        }
                        if (shown >= 200) break;
                    }
                }

                // 基准点(datum)画十字 + 分数/角度标注（卡尺精测覆盖时用红色大十字强调消费点；锚点画小绿十字参照）
                string posText = mo.RefinedByCaliper
                    ? $"✅ Score:{best.Score:F2}  Ang:{best.RotateDegree:F1}°  Datum({mo.DatumRow:F1},{mo.DatumCol:F1}) [{mo.RefineKind}]"
                    : $"✅ Score:{best.Score:F2}  Ang:{best.RotateDegree:F1}°  ({mo.DatumRow:F1},{mo.DatumCol:F1})";
                Preview?.AddCross(mo.DatumRow, mo.DatumCol, 44, mo.RefinedByCaliper ? "red" : "green");
                if (mo.RefinedByCaliper) Preview?.AddCross(best.PixelRow, best.PixelCol, 14, "green");
                Preview?.AddText(posText, 12, 12, "green");

                // 可视化贴合：模板特征轮廓刚体变换到匹配位置/角度后画在底图上——
                // 工件放置有角度和偏移，轮廓会跟着旋转平移，肉眼直接可判"模板贴没贴合上"。
                // 生成失败仅日志提示，不影响匹配结果与输出。
                var overlayRes = templateMgr.CreateMatchOverlay(param.TemplateName, best);
                if (overlayRes.Success)
                {
                    if (Preview != null)
                    {
                        Preview.Add(overlayRes.Data, "green", 2); // 提交即所有权转移给显示层
                    }
                    else
                    {
                        // 生产模式无预览窗口：释放避免 HALCON 句柄泄漏
                        NodePreviewHelper.DisposeQuietly(overlayRes.Data);
                    }
                }
                else
                {
                    context.Log($"[{node.DisplayName}] ⚠ 匹配贴合轮廓生成失败: {overlayRes.Message}（不影响匹配结果）");
                }
            }
            else
            {
                // 区分两种情况：① 匹配算子执行成功但未找到满足 MinScore 的候选（Success=true, Data.Match 空）；
                // ② 匹配链路异常（Success=false, Message 为真实原因）。
                // 注意 Result.Ok() 的默认 Message 是"执行正常"，直接把 Message 打出来会误导排查，
                // 必须显式拼接模板名与 MinScore 等诊断信息。
                string reason = outRes.Success
                    ? $"未找到 ≥ {param.MinScore:F2} 的匹配候选（0 个结果）"
                    : outRes.Message;
                context.Log($"[{node.DisplayName}] ❌ 模板匹配未命中: {reason}（模板: {param.TemplateName}, MinScore: {param.MinScore:F2}, 角度范围: {param.AngleStart:F0}°~{param.AngleEnd:F0}°, 输入: {(imgInfo.Success ? imgInfo.Data : imgInfo.Message)}, 耗时 {sw.ElapsedMilliseconds} ms）");

                // 失败场景保留底图（已 AddBorrowed）并叠加可读性更好的提示文本：
                // 用多行提示把常见原因与下一步操作直接写在画面上，避免操作员只看日志。
                string hint = outRes.Success
                    ? $"未找到匹配\n建议：1) 若角度>60°，请在模板管理重建模板并将角度范围设为 -180~180\n2) 调低 MinScore（当前 {param.MinScore:F2}）\n3) 检查光照/ROI/现场图像差异"
                    : $"匹配运算异常：{outRes.Message}";
                Preview?.AddText(hint, 12, 12, "red");
                Preview?.AddText($"模板: {param.TemplateName} | MinScore: {param.MinScore:F2} | 角度: {param.AngleStart:F0}°~{param.AngleEnd:F0}°", 12, 96, "yellow");
            }
        }
    }
}