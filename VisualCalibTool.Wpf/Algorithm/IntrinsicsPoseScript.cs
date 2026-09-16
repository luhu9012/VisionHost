using System;
using System.Collections.Generic;

namespace VisualCalibTool.Algorithm
{
    /// <summary>一次"请摆板"的提示（界面照它说话，操作员照它摆）。</summary>
    public sealed class PosePrompt
    {
        /// <summary>第几张（1 起）。</summary>
        public int Index;

        /// <summary>人话标题（"把板向左翘起来"）。</summary>
        public string Title;

        /// <summary>怎么摆（一句话，含具体角度 / 距离）。</summary>
        public string Hint;

        /// <summary>这一张期望的倾角（度），仅用于提示与进度显示。</summary>
        public double ExpectedTiltDeg;

        /// <summary>这一张相对基准位的远近变化（毫米，正 = 推远）。</summary>
        public double DeltaDepthMm;

        /// <summary>是否必须（false = 可选，跳过不影响判据）。</summary>
        public bool Required = true;

        public override string ToString()
        {
            return Title;
        }
    }

    /// <summary>
    /// ★ 摆板脚本：<b>"该怎么摆"这件事必须由工具说清楚</b>，不能丢一句"请多拍几张不同姿态"。
    ///
    /// 这份脚本本身要满足工具自己的覆盖度判据 —— 否则就成了"工具让用户做一套它自己都判不过的动作"。
    /// 所以 <see cref="IntrinsicsPoseScript"/> 有一条离线断言盯着它
    /// （脚本产出的姿态必须满足 TiltCount / MaxTiltDeg / DepthSpan / 方向数 四项）。
    ///
    /// 顺序也是有意排的：<b>先正对、再远近、最后倾斜</b>。
    ///   · 正对那张最容易成功，也用来确认"板文件与实物是不是同一块"；
    ///   · 远近先变化，是为了让操作员在板上还没翘起来时先建立"距离感"；
    ///   · 倾斜放最后：它最容易失败（定位图案会出视野），失败时前面的成果还在。
    /// </summary>
    public static class IntrinsicsPoseScript
    {
        /// <summary>默认倾斜角（度）。★ 20° 是实测下限，25~30° 更稳。</summary>
        public const double TiltDeg = 25.0;

        /// <summary>默认远近变化（毫米，单向）。</summary>
        public const double DepthStepMm = 25.0;

        /// <summary>推荐的最小张数（覆盖度判据要求至少 3 张倾斜，加正对与远近共 7 张起）。</summary>
        public const int RecommendedCount = 7;

        /// <summary>
        /// 生成摆板脚本。<paramref name="rich"/> = false 时只给最小集（正对 + 远 + 近 + 左右翘），
        /// 用于"先快速看一遍镜头有没有问题"的场景。
        /// </summary>
        public static List<PosePrompt> Build(bool rich = true)
        {
            var list = new List<PosePrompt>();
            int i = 1;

            list.Add(new PosePrompt
            {
                Index = i++,
                Title = "板放平、正对相机",
                Hint = "把板放在工作距离附近，让板【占画面宽度的一半以上】（七成左右最稳），"
                     + "四周留点边、别被切掉。板面尽量与镜头平行。"
                     + "这一张用来确认「板文件与实物是不是同一块」。"
                     + "\n\n为什么强调「占多大」：找板算法会把小于期望尺寸的 mark 当噪声整片剔掉，"
                     + "而它期望多大，取决于板看起来多大。板一远，检出率是断崖式掉到零"
                     + "（实测：板宽从画面的 49% 变成 47.6%，检出就从 818 个 mark 直接变成 0 个）——"
                     + "图上明明看得见板却一个都找不到。这种失败靠「再摆正一点」修不好，"
                     + "只能把板挪近。",
                ExpectedTiltDeg = 0.0,
                DeltaDepthMm = 0.0
            });

            list.Add(new PosePrompt
            {
                Index = i++,
                Title = "板放平，往镜头方向挪近",
                Hint = "保持板面与镜头平行，把板沿光轴（朝镜头方向）挪近约 "
                     + Fmt(DepthStepMm) + " mm。别改角度，只改距离。",
                ExpectedTiltDeg = 0.0,
                DeltaDepthMm = -DepthStepMm
            });

            list.Add(new PosePrompt
            {
                Index = i++,
                Title = "板放平，往远处挪",
                Hint = "保持板面与镜头平行，把板沿光轴远离镜头挪约 "
                     + Fmt(DepthStepMm) + " mm。",
                ExpectedTiltDeg = 0.0,
                DeltaDepthMm = DepthStepMm
            });

            list.Add(Tilt(i++, "把板向左翘起来", "左", 0.0, -TiltDeg));
            list.Add(Tilt(i++, "把板向右翘起来", "右", 0.0, TiltDeg));
            list.Add(Tilt(i++, "把板向前（朝自己）翘起来", "前", TiltDeg, 0.0));
            list.Add(Tilt(i++, "把板向后（朝远处）翘起来", "后", -TiltDeg, 0.0));

            if (rich)
            {
                list.Add(new PosePrompt
                {
                    Index = i++,
                    Title = "又斜又远（复合姿态）",
                    Hint = "把板向左翘 " + Fmt(TiltDeg) + "° 的同时，再往远处挪约 "
                         + Fmt(DepthStepMm) + " mm。两个方向同时变，才能把焦距和畸变分开。",
                    ExpectedTiltDeg = TiltDeg,
                    DeltaDepthMm = DepthStepMm
                });

                list.Add(new PosePrompt
                {
                    Index = i++,
                    Title = "换个方向的斜摆",
                    Hint = "把板向右下方向翘 " + Fmt(TiltDeg + 5.0) + "°（换成另一个斜向，别和前面重复），"
                         + "顺便绕板面法向转个 45~90°（板转圈不影响几何，但能让 mark 分布更均匀）。",
                    ExpectedTiltDeg = TiltDeg + 5.0,
                    DeltaDepthMm = 0.0,
                    Required = false
                });
            }

            return list;
        }

        private static PosePrompt Tilt(int index, string title, string dir, double dx, double dy)
        {
            return new PosePrompt
            {
                Index = index,
                Title = title,
                Hint = "保持板中心大致在画面里，绕板的横轴或竖轴把板面「翘」起来约 "
                     + Fmt(TiltDeg) + "°（向" + dir + "倾），倾斜后整块板仍要完整在画面内 —— "
                     + "倾斜太大时角上的定位图案会出视野，那就白拍了。",
                ExpectedTiltDeg = Math.Sqrt(dx * dx + dy * dy),
                DeltaDepthMm = 0.0
            };
        }

        private static string Fmt(double v)
        {
            return v.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
