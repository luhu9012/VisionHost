//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: 多目标预览上下文（扇出）——把同一批场景提交同时投给多个显示宿主。
//
// 存在原因（2026-09-15 定案）：IFlowPreviewContext 是"单目标"注入，
//   NodeExecutionContext.Preview 只能持有一个宿主，而 FlowVm 原来的注入策略是"二选一"
//   （属性面板打开 ⇒ 面板赢，主视图什么都拿不到）。于是节点执行期间的场景叠加层
//   （形状匹配的十字/分数文本/贴合轮廓、相机采集的标注等）只出现在**一个**窗口里。
//   症状：单步到「形状匹配」时，属性面板预览窗口能看到匹配效果，编辑器主视图却
//   只有底图——因为主视图只拿到"借用的同一张图"（MatchImage 与 InputImage 同一实例），
//   叠加层压根没投给它，看起来就像"没刷新"。
//
// 语义纪律：IFlowPreviewContext.Add 是"提交即所有权转移"（显示层负责释放）。
//   同一个 HObject 交给两个宿主 ⇒ 两边都会在清场景时 Dispose ⇒ 双重释放 / 另一方黑屏。
//   因此本类只让**第一个目标**接收原对象，其余目标一律接收 CopyForDisplay 副本。
//   AddBorrowed（底图）不转移所有权，可以安全地同时投给所有目标。
//===================================================================================

using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.HalconWrapper;
using System.Collections.Generic;
using System.Linq;

namespace Grayson.Vison.FlowEdit.Services
{
    /// <summary>
    /// 多目标预览上下文：把场景式绘制扇出到多个 IFlowPreviewContext。
    /// 典型装配 = [属性面板预览, 编辑器主视图预览]。
    /// 任一目标抛异常不影响其余目标（预览属于调试辅助，绝不能把执行链带崩）。
    /// </summary>
    public sealed class MultiTargetPreviewContext : IFlowPreviewContext
    {
        private readonly IFlowPreviewContext[] _targets;

        public MultiTargetPreviewContext(params IFlowPreviewContext[] targets)
        {
            _targets = (targets ?? new IFlowPreviewContext[0])
                .Where(t => t != null)
                .ToArray();
        }

        /// <summary>有效目标数。0 ⇒ 无处可画（等价于生产模式 Preview == null）。</summary>
        public int TargetCount => _targets.Length;

        /// <summary>任一目标就绪即视为可画（单个宿主窗口未就绪不应连累另一个）。</summary>
        public bool IsReady => _targets.Any(t => t.IsReady);

        /// <summary>
        /// 单目标时直接返回它（不包一层壳），多目标才包装——避免无谓的转发开销，
        /// 也让"只有主视图"这种最常见情形保持与改动前完全一致的对象身份。
        /// </summary>
        public static IFlowPreviewContext Combine(params IFlowPreviewContext[] targets)
        {
            var valid = (targets ?? new IFlowPreviewContext[0])
                .Where(t => t != null)
                .Distinct()
                .ToArray();

            if (valid.Length == 0) return null;
            if (valid.Length == 1) return valid[0];
            return new MultiTargetPreviewContext(valid);
        }

        public void BeginScene()
        {
            foreach (var t in _targets)
            {
                try { t.BeginScene(); } catch { /* 预览失败不影响其他目标与执行链 */ }
            }
        }

        public void AddBorrowed(object obj)
        {
            if (obj == null) return;
            foreach (var t in _targets)
            {
                try { t.AddBorrowed(obj); } catch { }
            }
        }

        /// <summary>
        /// 托管对象提交：★所有权只给第一个成功接收的目标，其余目标拿独立副本。
        /// 顺序遍历 + "第一个成功者接管所有权"，保证即使某个宿主已销毁/抛错，
        /// 原对象也不会既没人释放、又被下一个目标当成"自己的"而重复释放。
        /// </summary>
        public void Add(object obj, string color = null, int lineWidth = 1)
        {
            if (obj == null) return;

            var ownerAssigned = false;
            foreach (var t in _targets)
            {
                if (!ownerAssigned)
                {
                    try
                    {
                        t.Add(obj, color, lineWidth);
                        ownerAssigned = true; // 接手成功：原对象所有权已转移给它
                    }
                    catch
                    {
                        // 该宿主接收失败：保持 ownerAssigned=false，让下一个目标接手原对象
                    }
                    continue;
                }

                // 后续目标：必须给副本（副本所有权转移给该目标；复制失败则跳过，绝不投 null）
                var copy = NodePreviewHelper.CopyForDisplay(obj);
                if (copy == null) continue;
                try
                {
                    t.Add(copy, color, lineWidth);
                }
                catch
                {
                    NodePreviewHelper.DisposeQuietly(copy); // 目标拒收 ⇒ 就地释放，防句柄泄漏
                }
            }
        }

        public void AddText(string text, double row, double col, string color)
        {
            foreach (var t in _targets)
            {
                try { t.AddText(text, row, col, color); } catch { }
            }
        }

        public void AddCross(double row, double col, double size, string color)
        {
            foreach (var t in _targets)
            {
                try { t.AddCross(row, col, size, color); } catch { }
            }
        }

        public void AddCircle(double row, double col, double radius, string color)
        {
            foreach (var t in _targets)
            {
                try { t.AddCircle(row, col, radius, color); } catch { }
            }
        }
    }
}
