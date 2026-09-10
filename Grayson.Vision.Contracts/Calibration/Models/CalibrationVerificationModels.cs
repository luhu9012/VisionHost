//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationVerificationModels.cs
// 说 明: P3 标定校验台会话模型（2026-09-05）。
//        标定校验台 = 发布前/复验的人工闭环：图上点选实物特征 → 建议机械坐标(不动轴)
//        → 低速到位(门面) → 目视判定 通过/偏移 → 逐点记录，形成可回查的验收证据链。
//        范围拍板（2026-09-05）：一期只做"在线打点验收"(B 块)；残差反投影体检(A 块)
//        预留后续；Samples 快照随 profile 落盘为 A 块铺数据源。
//===================================================================================
using System;
using System.Collections.Generic;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;

namespace Grayson.Vision.Contracts.Calibration.Models
{
    /// <summary>校验台单次"打点"条目（一次点选+到位+判定）</summary>
    public class CalibrationVerificationPoint : ViewModelBase
    {
        private int _order;
        /// <summary>验证点序号（1..N）</summary>
        public int Order
        {
            get => _order;
            set => Set(ref _order, value);
        }

        private double _pixelX;
        /// <summary>图上点选的实物特征像素 X</summary>
        public double PixelX
        {
            get => _pixelX;
            set => Set(ref _pixelX, value);
        }

        private double _pixelY;
        /// <summary>图上点选的实物特征像素 Y</summary>
        public double PixelY
        {
            get => _pixelY;
            set => Set(ref _pixelY, value);
        }

        private double _worldX;
        /// <summary>建议机械坐标 X（MapPixelToWorld 输出；仅建议，到位前不动轴）</summary>
        public double WorldX
        {
            get => _worldX;
            set => Set(ref _worldX, value);
        }

        private double _worldY;
        /// <summary>建议机械坐标 Y</summary>
        public double WorldY
        {
            get => _worldY;
            set => Set(ref _worldY, value);
        }

        private bool _passed;
        /// <summary>目视判定是否通过（工具头对准目标）</summary>
        public bool Passed
        {
            get => _passed;
            set
            {
                if (Set(ref _passed, value))
                {
                    OnPropertyChanged(nameof(StatusText));
                }
            }
        }

        private double? _offsetMm;
        /// <summary>偏差时的目视估计偏移量（mm；通过时可为空）</summary>
        public double? OffsetMm
        {
            get => _offsetMm;
            set => Set(ref _offsetMm, value);
        }

        private string _note;
        /// <summary>现场备注（可选）</summary>
        public string Note
        {
            get => _note;
            set => Set(ref _note, value);
        }

        /// <summary>判定状态文本（列表/汇总展示）</summary>
        public string StatusText => Passed ? "✅ 通过" : "❌ 偏差";

        /// <summary>是否已判定（未判定不参与统计）</summary>
        public bool IsVerdicted => Passed || OffsetMm != null;
    }

    /// <summary>校验台一次完整验收会话记录（挂 CalibrationProfile.VerificationRecords）</summary>
    public class CalibrationVerificationRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>验收完成时间</summary>
        public DateTime VerifiedTime { get; set; } = DateTime.Now;

        /// <summary>验证点总数</summary>
        public int TotalPoints { get; set; }

        /// <summary>判定通过数</summary>
        public int PassedCount { get; set; }

        /// <summary>通过率 0~100</summary>
        public double PassRate => TotalPoints > 0 ? Math.Round(PassedCount * 100.0 / TotalPoints, 1) : 0;

        /// <summary>校验者备注（可选）</summary>
        public string Note { get; set; }

        /// <summary>
        /// 记录类别（P3 发布留痕用；旧校验台记录为 null 不受影响）：
        ///   null        = 校验台验收会话（残差体检 A 块 / 在线打点 B 块）
        ///   "Publish"   = 发布门禁通过（体检全绿 → 发布）
        ///   "PublishBypass" = 「我知道风险」旁路发布（红项在场但用户确认，Note 记原因——留痕）
        /// 任务卡状态机据 Kind 识别"已发布"（见 CalibrationCardDeriver）。
        /// </summary>
        public string Kind { get; set; }

        /// <summary>
        /// 发布时刻的数据指纹（P4 快照联动，2026-09-05 夜）：发布时把该量的关键字段串成指纹
        /// （见 CalibrationCardDeriver.BuildSnapshotKey）。Derive 时若当前档案指纹 ≠ 发布快照
        /// → 该量卡降级 Expired（"发布后数据已变更，需重标"）。
        /// null = 旧记录（发布标记机制上线前/校验台验收记录）→ 不做快照比对，不误伤。
        /// </summary>
        public string SnapshotKey { get; set; }

        /// <summary>逐点明细</summary>
        public List<CalibrationVerificationPoint> Points { get; set; } = new List<CalibrationVerificationPoint>();
    }
}
