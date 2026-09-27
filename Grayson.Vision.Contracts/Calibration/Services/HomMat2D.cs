//===================================================================================
// 文件名: HomMat2D.cs
// 说 明: 2D 仿射矩阵的**纯算术**读取与映射（不依赖 Halcon 运行时）。
//
// 为什么需要它（2026-09-16）：
//   像素→世界的映射原先只有一处实现：`CalibrationService.MapPixelToWorld`（HalconWrapper），
//   内部就两步 —— ① `Calib2DTool.LoadHomMatFromFile`（读 .tup 文本成 6 个 double）
//   ② `HOperatorSet.AffineTransPoint2d`（一次 2×3 仿射点乘）。
//   而 `Grayson.Vision.Core` **不引用 HalconWrapper**（csproj 只引 Contracts + Repository），
//   于是生产端唯一算不了的派生量就是"下相机轴投影常量"：H_down(R_cdown)。
//   这曾导致一个两难：为了算它就要给 Core 加 Halcon 依赖，或把活推给插件的 CalibrationApply 节点
//   （改插件风险更高）。
//
//   ★ 本类的存在让这个两难消失：仿射就是 6 个乘加，**不需要 Halcon**。
//   实测佐证（2026-09-16，ST_002 Cam_C）：
//     H_down = [0.000309, 0.033933, 58.380364, −0.034023, −0.000001, −170.562403]
//     R_cdown = (1472.212347, 988.329069)px
//     ⇒ H_down(R_cdown) = (92.3723213, −220.6523300)mm
//     vs 发布链写进工位配置的 (92.37232, −220.652328) ⇒ 差 2e−6mm（float32 存储舍入量级）
//   ⇒ 判据：**同值**。故生产端可自行复算，不必再维护"发布链抄一份常量"。
//
// 单元纪律：
//   · 本类**只读**（读文件、算数），不写任何东西、不抛异常（失败用 out error 表达）。
//   · 不做"梯度/条件数"等质量判定 —— 那种判断要用真奇异值，别在这里顺手做列范数近似
//     （两者在近似正交时接近但不等，用错等于把验收门开大）。
//===================================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Grayson.Vision.Contracts.Calibration.Models;

namespace Grayson.Vision.Contracts.Calibration.Services
{
    /// <summary>
    /// Halcon 2D 齐次仿射矩阵（元素顺序 <c>[a11, a12, tx, a21, a22, ty]</c>，与
    /// <c>hom_mat2d_identity</c> = [1,0,0,0,1,0] 一致）的纯托管实现。
    /// 映射式：<c>x' = a11·px + a12·py + tx</c>，<c>y' = a21·px + a22·py + ty</c>。
    /// </summary>
    public sealed class HomMat2D
    {
        public double A11 { get; private set; }
        public double A12 { get; private set; }
        public double Tx { get; private set; }
        public double A21 { get; private set; }
        public double A22 { get; private set; }
        public double Ty { get; private set; }

        /// <summary>读到矩阵的来源文件路径（日志用；未从文件读时为 null）</summary>
        public string SourceFile { get; private set; }

        /// <summary>行列式：负 = 含镜像（仰视相机常见），≈0 = 退化（不可用）</summary>
        public double Determinant => A11 * A22 - A12 * A21;

        /// <summary>像素点 → 世界点</summary>
        public void Map(double px, double py, out double wx, out double wy)
        {
            wx = A11 * px + A12 * py + Tx;
            wy = A21 * px + A22 * py + Ty;
        }

        /// <summary>
        /// 从 .tup 文件读取。文件格式（Halcon 元组序列化）：
        /// <code>
        /// 06
        /// 2 1.4251702546747316e+02
        /// ...
        /// </code>
        /// 第 1 行是元组个数，其后每行 <c>&lt;类型码&gt; &lt;数值&gt;</c>。
        /// 兼容两种退化写法：整份文件只有 6 个纯数字（无头行 / 一行 6 个数）。
        /// </summary>
        public static bool TryLoad(string filePath, out HomMat2D mat, out string error)
        {
            mat = null;
            error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    error = "矩阵文件不存在: " + (filePath ?? "(null)");
                    return false;
                }
                string[] raw = File.ReadAllLines(filePath);
                var lines = new List<string>();
                foreach (var l in raw)
                {
                    if (!string.IsNullOrWhiteSpace(l)) lines.Add(l.Trim());
                }
                if (lines.Count == 0)
                {
                    error = "矩阵文件为空";
                    return false;
                }

                var nums = new List<double>();
                // 形态 A：头行 = 个数，其后每行取**最后一个** token（跳过类型码）
                int head;
                bool headOk = int.TryParse(lines[0].Trim(), NumberStyles.Integer,
                                           CultureInfo.InvariantCulture, out head);
                if (headOk && head == 6)
                {
                    for (int i = 1; i < lines.Count; i++)
                    {
                        double v;
                        if (TryLastToken(lines[i], out v)) nums.Add(v);
                    }
                }
                // 形态 B：整份当作数字流（一行一个或一行多个都吃）
                if (nums.Count != 6)
                {
                    nums.Clear();
                    foreach (var l in lines)
                    {
                        foreach (var tok in l.Split(new[] { ' ', '\t', ',', ';' },
                                                     StringSplitOptions.RemoveEmptyEntries))
                        {
                            double v;
                            if (double.TryParse(tok, NumberStyles.Float,
                                                CultureInfo.InvariantCulture, out v))
                            {
                                nums.Add(v);
                            }
                        }
                    }
                }
                if (nums.Count != 6)
                {
                    error = "矩阵元组个数不是 6（读得 " + nums.Count + "）: " + filePath;
                    return false;
                }

                mat = new HomMat2D
                {
                    A11 = nums[0], A12 = nums[1], Tx = nums[2],
                    A21 = nums[3], A22 = nums[4], Ty = nums[5],
                    SourceFile = filePath,
                };
                if (Math.Abs(mat.Determinant) < 1e-12)
                {
                    error = "矩阵退化（行列式≈0），不可用于映射: " + filePath;
                    mat = null;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "读取矩阵失败: " + ex.Message;
                mat = null;
                return false;
            }
        }

        /// <summary>
        /// 按档案解析矩阵路径后读取（路径真源 = <see cref="CalibrationMatrixStore.ResolveMatrixPath"/>，
        /// 单轨存储：工位级目录；设备级已废弃）。
        /// </summary>
        public static bool TryLoadForProfile(CalibrationProfile profile, out HomMat2D mat, out string error)
        {
            string path = CalibrationMatrixStore.ResolveMatrixPath(profile);
            if (string.IsNullOrWhiteSpace(path))
            {
                mat = null;
                error = "未解析到矩阵文件（档案「" + (profile?.Name ?? "?")
                        + "」的 HomMatFilePath 与工位级标准产物名均不存在）";
                return false;
            }
            return TryLoad(path, out mat, out error);
        }

        private static bool TryLastToken(string line, out double value)
        {
            value = 0;
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;
            return double.TryParse(parts[parts.Length - 1], NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out value);
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "[{0:G9},{1:G9},{2:G9},{3:G9},{4:G9},{5:G9}]",
                A11, A12, Tx, A21, A22, Ty);
        }
    }
}
