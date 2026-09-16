using System;
using System.Globalization;
using HalconDotNet;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Imaging
{
    /// <summary>
    /// ★ <c>.tup</c> 的<b>真算子</b>读写（用 <see cref="HOperatorSet.WriteTuple"/> / <c>ReadTuple</c>）。
    ///
    /// 为什么同时存在两条写入路径：
    ///   · <c>Algorithm/HomMatIO</c> 是纯 C# 手写（离线可测、逐位可控、不依赖 HALCON 运行时）；
    ///   · 本类是 HALCON 原生写（主项目 <c>Calib2DTool</c> 正是用它读的）。
    ///
    /// <b>两条路径的输出必须逐位一致</b> —— 这不是洁癖，而是唯一的证明方式：
    ///   如果它们不一致，说明"主项目读到的矩阵"和"我们算出来给用户看的矩阵"不是同一个东西，
    ///   而 H 的语义恰恰就是"差一点点落点就偏一点点"。这条断言已进离线自检。
    ///
    /// ★ 参数个数是 6（2×3 仿射），不是 9。已用真算子实测确认：
    ///   <c>vector_to_hom_mat2d</c> 在点对应上返回<b>恰好 6 个</b>参数（"Approximate an affine transformation"），
    ///   与本工位真机产物的 155 字节 .tup 完全吻合。
    /// </summary>
    public static class HalconTupleIO
    {
        /// <summary>写 6 参数仿射到 .tup（HALCON 元组文件）。</summary>
        public static bool TryWriteTup(string path, HomMat2D h, out string error)
        {
            error = null;
            try
            {
                double[] v = h.ToArray();
                var tuple = new HTuple(v);
                HOperatorSet.WriteTuple(tuple, new HTuple(path));
                return true;
            }
            catch (Exception ex)
            {
                error = "写 .tup 失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>读 .tup 到 6 参数仿射。元素个数不是 6 即视为非法（宁可失败也不猜）。</summary>
        public static bool TryReadTup(string path, out HomMat2D h, out string error)
        {
            h = HomMat2D.Identity;
            error = null;

            try
            {
                HOperatorSet.ReadTuple(new HTuple(path), out HTuple tuple);
                if (tuple.Length != 6)
                {
                    error = string.Format(CultureInfo.InvariantCulture,
                        ".tup 里有 {0} 个值，本工具要求恰好 6 个（2×3 仿射）。"
                        + "9 个值意味着这是一个射影矩阵，消费语义不同，不能当仿射用。", tuple.Length);
                    return false;
                }

                double[] v = new double[6];
                for (int i = 0; i < 6; i++)
                {
                    v[i] = tuple[i].D;
                }

                h = HomMat2D.FromArray(v);
                return true;
            }
            catch (Exception ex)
            {
                error = "读 .tup 失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// ★ 用真算子做点对应解算（<c>vector_to_hom_mat2d</c>），与工具的纯代数解算器互为独立实现。
        /// 两者在同一批点上结果一致，才说明"我们自己写的仿射最小二乘没写歪"。
        /// </summary>
        public static bool TrySolveHomMat2D(double[] px, double[] py, double[] wx, double[] wy,
            out HomMat2D h, out string error)
        {
            h = HomMat2D.Identity;
            error = null;

            try
            {
                if (px == null || py == null || wx == null || wy == null
                    || px.Length != py.Length || px.Length != wx.Length || px.Length != wy.Length
                    || px.Length < 3)
                {
                    error = "点对应数据不足（至少 3 对，且四个数组等长）。";
                    return false;
                }

                HOperatorSet.VectorToHomMat2d(new HTuple(px), new HTuple(py), new HTuple(wx), new HTuple(wy),
                    out HTuple hom);

                if (hom.Length != 6)
                {
                    error = string.Format(CultureInfo.InvariantCulture,
                        "vector_to_hom_mat2d 返回了 {0} 个参数（预期 6 个仿射）。", hom.Length);
                    return false;
                }

                double[] v = new double[6];
                for (int i = 0; i < 6; i++)
                {
                    v[i] = hom[i].D;
                }

                h = HomMat2D.FromArray(v);
                return true;
            }
            catch (Exception ex)
            {
                error = "vector_to_hom_mat2d 失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>本机 HALCON 版本（诊断/溯源用）。取不到返回 null。</summary>
        public static string TryGetHalconVersion()
        {
            try
            {
                HOperatorSet.GetSystem(new HTuple("version"), out HTuple v);
                return v.Length > 0 ? v[0].S : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
