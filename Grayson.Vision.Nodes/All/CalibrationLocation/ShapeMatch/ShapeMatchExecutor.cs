using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Contracts.Templates.Models;
using Grayson.Vision.HalconWrapper.Match2D;
using Grayson.Vision.HalconWrapper.Templates;
using Grayson.Vision.HalconWrapper.ImageProc;
using Grayson.Vision.HalconWrapper;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.CalibrationLocation.ShapeMatch
{
    [Node(NodeType.ShapeMatch, NodeCategory.CalibrationLocation, typeof(ShapeMatchParam))]
    [NodePort("InputImage", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#9B59B6")]
    [NodePort("MatchRow", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#E74C3C")]
    [NodePort("MatchCol", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#E74C3C")]
    [NodePort("MatchAngle", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#E74C3C")]
    [NodePort("MatchScore", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#2ECC71")]
    public class ShapeMatchExecutor : NodeExecutorBase<ShapeMatchParam>
    {
        public const string PORT_IN_IMAGE = "InputImage";
        public const string PORT_OUT_ROW = "MatchRow";
        public const string PORT_OUT_COL = "MatchCol";
        public const string PORT_OUT_ANGLE = "MatchAngle";
        public const string PORT_OUT_SCORE = "MatchScore";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, ShapeMatchParam param, NodeExecutionContext context, CancellationToken token)
        {
            await Task.Yield();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            object inputImage = context.GetInputValue<object>(node, PORT_IN_IMAGE);
            if (inputImage == null)
            {
                context.Log("[" + node.DisplayName + "] 错误：未获取到有效输入图像句柄！");
                Preview?.BeginScene();
                Preview?.AddText("⚠️ 输入图像为空，请先运行上游节点", 12, 12, "red");
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
            }
            else
            {
                context.Log($"[{node.DisplayName}] ⚠ 模板元信息读取失败: {infoRes.Message}（匹配继续执行，若模板文件缺失会在此失败）");
            }

            // 预览：输入图为底图
            Preview?.BeginScene();
            Preview?.AddBorrowed(inputImage);

            // 运行时经模板管理按名完成"加载句柄→匹配→释放"全流程（句柄类型封装在 HalconWrapper 层，不泄漏到 Nodes）
            var matchRes = templateMgr.MatchByName(param.TemplateName, inputImage, param.MinScore);
            sw.Stop();

            if (matchRes.Success && matchRes.Data != null && matchRes.Data.Length > 0)
            {
                var best = matchRes.Data[0];
                context.SetOutputValue(node, PORT_OUT_ROW, best.PixelRow);
                context.SetOutputValue(node, PORT_OUT_COL, best.PixelCol);
                context.SetOutputValue(node, PORT_OUT_ANGLE, best.RotateDegree);
                context.SetOutputValue(node, PORT_OUT_SCORE, best.Score);

                // 诊断日志③：候选明细——不止最佳，全部候选逐一列出，单步调试时可观察次优候选的分数梯度
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < matchRes.Data.Length; i++)
                {
                    var r = matchRes.Data[i];
                    sb.Append($"[{i + 1}] Row={r.PixelRow:F2} Col={r.PixelCol:F2} Ang={r.RotateDegree:F2}° Score={r.Score:F3}  ");
                }
                context.Log($"[{node.DisplayName}] ✅ 形状匹配成功，候选 {matchRes.Data.Length} 个，耗时 {sw.ElapsedMilliseconds} ms");
                context.Log($"[{node.DisplayName}]    最佳: Row={best.PixelRow:F2} Col={best.PixelCol:F2} Ang={best.RotateDegree:F2}° Score={best.Score:F3}");
                context.Log($"[{node.DisplayName}]    候选明细: {sb}");

                // 匹配位置画十字 + 分数/角度标注
                Preview?.AddCross(best.PixelRow, best.PixelCol, 40, "green");
                Preview?.AddText($"✅ Score: {best.Score:F2}  Angle: {best.RotateDegree:F1}°  ({best.PixelRow:F1}, {best.PixelCol:F1})", 12, 12, "green");

                // 可视化贴合：模板特征轮廓刚体变换到匹配位置/角度后画在底图上——
                // 麻将放置有角度和偏移，轮廓会跟着旋转平移，肉眼直接可判"模板贴没贴合上"。
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
                // 区分两种情况：① 匹配算子执行成功但未找到满足 MinScore 的候选（Success=true, Data 空）；
                // ② 匹配链路异常（Success=false, Message 为真实原因）。
                // 注意 Result.Ok() 的默认 Message 是"执行正常"，直接把 Message 打出来会误导排查，
                // 必须显式拼接模板名与 MinScore 等诊断信息。
                string reason = matchRes.Success
                    ? $"未找到 ≥ {param.MinScore:F2} 的匹配候选（0 个结果），建议调低 MinScore 或检查模板/现场图像差异"
                    : matchRes.Message;
                context.Log($"[{node.DisplayName}] ❌ 形状匹配未命中: {reason}（模板: {param.TemplateName}, MinScore: {param.MinScore:F2}, 输入: {(imgInfo.Success ? imgInfo.Data : imgInfo.Message)}, 耗时 {sw.ElapsedMilliseconds} ms）");
                Preview?.AddText($"❌ {reason}", 12, 12, "red");
            }
        }
    }
}