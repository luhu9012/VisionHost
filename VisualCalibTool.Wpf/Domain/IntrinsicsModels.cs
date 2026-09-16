using System;
using System.Globalization;

namespace VisualCalibTool.Domain
{
    /// <summary>
    /// ★ 相机内参的<b>初始猜测</b>：真机上"能填得出来"的那几个数。
    ///
    /// 为什么不叫"内参"而叫"猜测"：<c>calibrate_cameras</c> 需要一个起点
    /// （<c>set_calib_data_cam_param</c> 给的就是起步值），它随后会自己迭代到解。
    /// 起点给得离谱会不收敛，给得粗糙完全没关系 —— 所以这里刻意只要求
    /// <b>标称焦距 + 像素尺寸 + 图像尺寸</b>，主点默认图像中心、畸变默认 0，
    /// 不要求操作员先去查相机手册填一堆参数。
    ///
    /// ★ 量纲（这里最容易错）：HALCON 的相机参数里 <b>焦距、像素尺寸、主点单位都是米</b>，
    ///   不是像素、不是毫米。焦距 11.84 mm 必须写成 <c>0.01184</c>。
    ///   写错的后果是"标定成功但焦距差了三个数量级"，而且残差可能依然很小。
    /// </summary>
    public sealed class CameraIntrinsicsGuess
    {
        /// <summary>相机模型。<c>area_scan_division</c> 是面阵相机 + 除法畸变模型（本工具默认）。</summary>
        public string CameraType = "area_scan_division";

        /// <summary>焦距（米）。真机填镜头标称值即可，例如 12 mm 镜头 → 0.012。</summary>
        public double FocalM = 0.012;

        /// <summary>径向畸变 kappa（1/m²，HALCON 除法模型口径）。起点填 0 即可。</summary>
        public double Kappa;

        /// <summary>像元尺寸（米）。3.45 µm → 3.45e-6。</summary>
        public double PixelPitchM = 3.45e-6;

        /// <summary>主点 X（像素）。未测时填图像中心。</summary>
        public double Cx;

        /// <summary>主点 Y（像素）。未测时填图像中心。</summary>
        public double Cy;

        public int Width = 1280;
        public int Height = 1024;

        /// <summary>按图像尺寸与标称值造一个猜测（主点取图像中心、畸变取 0）。</summary>
        public static CameraIntrinsicsGuess ForImage(int width, int height, double focalMm, double pixelPitchUm)
        {
            var g = new CameraIntrinsicsGuess();
            g.Width = width > 0 ? width : 1280;
            g.Height = height > 0 ? height : 1024;
            g.FocalM = focalMm > 0 ? focalMm / 1000.0 : 0.012;
            g.PixelPitchM = pixelPitchUm > 0 ? pixelPitchUm / 1e6 : 3.45e-6;
            g.Cx = (g.Width - 1) / 2.0;
            g.Cy = (g.Height - 1) / 2.0;
            g.Kappa = 0.0;
            return g;
        }

        /// <summary>焦距换算成像素（f / 像元），用来判断"这个起点离不离谱"。</summary>
        public double FocalPx
        {
            get { return PixelPitchM > 1e-12 ? FocalM / PixelPitchM : double.NaN; }
        }

        /// <summary>
        /// 视场角（对角，度）。★ 这是给操作员的"体检数"：
        /// 焦距填错时视野角会明显不对，比"焦距 0.012 m"这种数字直观得多。
        /// </summary>
        public double DiagonalFovDeg
        {
            get
            {
                if (PixelPitchM <= 1e-12 || FocalM <= 1e-12)
                {
                    return double.NaN;
                }

                double w = Width * PixelPitchM;
                double h = Height * PixelPitchM;
                double diag = Math.Sqrt(w * w + h * h);
                return 2.0 * Math.Atan(diag / 2.0 / FocalM) * 180.0 / Math.PI;
            }
        }

        public string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0}：焦距 {1:F3} mm（≈{2:F0} px），像元 {3:F2} µm，图像 {4}×{5}，"
                + "主点起点 ({6:F1}, {7:F1})，畸变起点 {8:F1}，对角视场 {9:F1}°",
                CameraType, FocalM * 1000.0, FocalPx, PixelPitchM * 1e6, Width, Height,
                Cx, Cy, Kappa, DiagonalFovDeg);
        }

        public override string ToString()
        {
            return Describe();
        }
    }
}
