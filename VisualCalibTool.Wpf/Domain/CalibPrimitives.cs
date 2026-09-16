using System;
using System.Globalization;

namespace VisualCalibTool.Domain
{
    /// <summary>
    /// 二维点 / 向量。本工具自持（不借用 Contracts 类型），保证 Algorithm 层零外部依赖、可离线单测。
    /// </summary>
    public struct Vec2 : IEquatable<Vec2>
    {
        public double X;
        public double Y;

        public Vec2(double x, double y)
        {
            X = x;
            Y = y;
        }

        public static Vec2 Zero
        {
            get { return new Vec2(0, 0); }
        }

        public static Vec2 operator +(Vec2 a, Vec2 b)
        {
            return new Vec2(a.X + b.X, a.Y + b.Y);
        }

        public static Vec2 operator -(Vec2 a, Vec2 b)
        {
            return new Vec2(a.X - b.X, a.Y - b.Y);
        }

        public static Vec2 operator *(Vec2 a, double k)
        {
            return new Vec2(a.X * k, a.Y * k);
        }

        public static Vec2 operator *(double k, Vec2 a)
        {
            return new Vec2(a.X * k, a.Y * k);
        }

        public static Vec2 operator /(Vec2 a, double k)
        {
            return new Vec2(a.X / k, a.Y / k);
        }

        public double Length
        {
            get { return Math.Sqrt(X * X + Y * Y); }
        }

        public double LengthSq
        {
            get { return X * X + Y * Y; }
        }

        public bool IsFinite
        {
            get { return !double.IsNaN(X) && !double.IsInfinity(X) && !double.IsNaN(Y) && !double.IsInfinity(Y); }
        }

        /// <summary>绕原点逆时针旋转 <paramref name="deg"/> 度。</summary>
        public Vec2 Rotate(double deg)
        {
            double r = deg * Math.PI / 180.0;
            double c = Math.Cos(r);
            double s = Math.Sin(r);
            return new Vec2(c * X - s * Y, s * X + c * Y);
        }

        public double DistanceTo(Vec2 other)
        {
            return (this - other).Length;
        }

        public bool Equals(Vec2 other)
        {
            return X.Equals(other.X) && Y.Equals(other.Y);
        }

        public override bool Equals(object obj)
        {
            return obj is Vec2 && Equals((Vec2)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (X.GetHashCode() * 397) ^ Y.GetHashCode();
            }
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "({0:F4}, {1:F4})", X, Y);
        }
    }

    /// <summary>
    /// 2D 仿射变换 H = [h11 h12 h13; h21 h22 h23]（6 自由度）。
    ///
    /// ★ 语义与主项目 <c>.tup</c> 逐位一致：把【像素坐标 u】映射到【世界坐标 P（法兰命令位域）】。
    ///   即 H(u) = P："要让基准特征出现在像素 u，机械手该停在哪"。
    ///   ★ 注意这是"落点式"口径，消费时 H 必须与 H(u) 做差分，不可直接当作目标位置使用。
    /// </summary>
    public struct HomMat2D
    {
        /// <summary>
        /// 判"能不能求逆"的 |det| 下限 —— <b>全项目唯一的那个数</b>。
        ///
        /// ★★ 为什么要把它提出来当常量并配一条 <see cref="IsInvertible"/>（2026-09-14）：
        ///   修前这个 `1e-15` 被**抄了三遍** —— `Invert()` 里一遍、
        ///   `ReprojectionAnalyzer.Analyze` 里一遍、`ReprojectionAnalyzer.ProjectPlan` 里又一遍，
        ///   而且**符号方向还不一样**（`Invert()` 用 `&lt;` 拦、`Analyze` 用 `&gt;` 放）。
        ///   当时恰好互相安全（`|det| &gt; 1e-15` ⇒ `Invert()` 必不抛），
        ///   但那是**靠巧合成立的正确**：改任一处、或哪天觉得"1e-15 太松"调一下，
        ///   就会变成"判据说可逆 → `Invert()` 当场抛异常"（`Analyze` 里那次调用**没有 try/catch**）。
        ///   ⇒ 现在判据与行为**同源**：`Invert()` 的守卫就是 `if (!IsInvertible) throw`。
        /// </summary>
        public const double MinAbsDet = 1e-15;

        public double H11, H12, H13;
        public double H21, H22, H23;

        public HomMat2D(double h11, double h12, double h13, double h21, double h22, double h23)
        {
            H11 = h11; H12 = h12; H13 = h13;
            H21 = h21; H22 = h22; H23 = h23;
        }

        public static HomMat2D Identity
        {
            get { return new HomMat2D(1, 0, 0, 0, 1, 0); }
        }

        public bool IsFinite
        {
            get
            {
                return !double.IsNaN(H11) && !double.IsInfinity(H11)
                    && !double.IsNaN(H12) && !double.IsInfinity(H12)
                    && !double.IsNaN(H13) && !double.IsInfinity(H13)
                    && !double.IsNaN(H21) && !double.IsInfinity(H21)
                    && !double.IsNaN(H22) && !double.IsInfinity(H22)
                    && !double.IsNaN(H23) && !double.IsInfinity(H23);
            }
        }

        /// <summary>代数余子式行列式 det(A) = h11*h22 - h12*h21。负值 ⇒ 含镜像（须告警）。</summary>
        public double Det
        {
            get { return H11 * H22 - H12 * H21; }
        }

        /// <summary>把像素点映射到世界坐标（法兰命令位域）。</summary>
        public Vec2 Transform(Vec2 pixel)
        {
            return new Vec2(
                H11 * pixel.X + H12 * pixel.Y + H13,
                H21 * pixel.X + H22 * pixel.Y + H23);
        }

        /// <summary>
        /// 仅旋转分量作用于"增量向量"（差分用法）：
        /// ΔP = R · Δu，与平移项无关。用于 H(u2)-H(u1) 这类消费。
        /// </summary>
        public Vec2 TransformDelta(Vec2 pixelDelta)
        {
            return new Vec2(
                H11 * pixelDelta.X + H12 * pixelDelta.Y,
                H21 * pixelDelta.X + H22 * pixelDelta.Y);
        }

        /// <summary>
        /// 这个矩阵<b>能不能求逆</b> —— 判据就是 <see cref="Invert"/> 的守卫本身。
        ///
        /// ★ 定义取 <c>&gt;=</c>（而不是 `&gt;`）：它的语义是"<b>调用 <see cref="Invert"/> 会不会抛</b>"。
        ///   修前 `Analyze` 用 `&gt;`、`Invert()` 用 `&lt;`，在**恰好等于**阈值那一点上两者不一致
        ///   （判据说"不可逆"、而 `Invert()` 其实会成功）—— 属于"判据比实际行为更保守"，
        ///   不会崩，但**判据名不副实**。现在两边都由这一个表达式派生，边界上没有第二种说法。
        ///   ★ 纯代数层有一条断言专门钉这一点（阈值近邻三点 + `IsInvertible == !Throws(Invert)`）。
        /// </summary>
        public bool IsInvertible
        {
            get { return Math.Abs(Det) >= MinAbsDet; }
        }

        /// <summary>逆变换（仿射矩阵可逆时）。</summary>
        public HomMat2D Invert()
        {
            // ★ 守卫用的是 IsInvertible 本身 —— 判据与行为同源，不可能再分叉。
            if (!IsInvertible)
            {
                throw new InvalidOperationException("HomMat2D 不可逆（det≈0）");
            }

            double det = Det;
            double i11 = H22 / det;
            double i12 = -H12 / det;
            double i21 = -H21 / det;
            double i22 = H11 / det;
            return new HomMat2D(
                i11, i12, -(i11 * H13 + i12 * H23),
                i21, i22, -(i21 * H13 + i22 * H23));
        }

        /// <summary>按 [h11 h12 h13 h21 h22 h23] 顺序（即 .tup 的写入顺序）导出。</summary>
        public double[] ToArray()
        {
            return new double[] { H11, H12, H13, H21, H22, H23 };
        }

        /// <summary>按 [h11 h12 h13 h21 h22 h23] 顺序读入。</summary>
        public static HomMat2D FromArray(double[] v)
        {
            if (v == null || v.Length < 6)
            {
                throw new ArgumentException("HomMat2D 需要 6 个参数 [h11 h12 h13 h21 h22 h23]", "v");
            }

            return new HomMat2D(v[0], v[1], v[2], v[3], v[4], v[5]);
        }

        public override string ToString()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "H=[{0:G8} {1:G8} {2:G8}; {3:G8} {4:G8} {5:G8}]",
                H11, H12, H13, H21, H22, H23);
        }
    }
}
