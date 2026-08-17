using System;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Core
{
    /// <summary>
    /// 全局通用工具：Pose互转、像素/毫米换算、HWindow绘图基础封装
    /// 适配全版本Halcon，使用标准AffineTransPoint2d做坐标转换
    /// 坐标系：图像列=X(Column)，行=Y(Row)；Pose角度单位°；世界坐标mm
    /// </summary>
    public static class HalconGlobalHelper
    {
        /// <summary>默认十字线像素长度</summary>
        public const double DefaultCrossLength = 20d;
        /// <summary>默认绘图颜色</summary>
        public const string DefaultDrawColor = "red";
        /// <summary>Pose固定6个参数</summary>
        private const int PoseElementCount = 6;

        #region Pose 双向转换
        public static Pose3D HtuplePoseToPose3D(HTuple hPose)
        {
            if (hPose == null || hPose.Length != PoseElementCount)
            {
                LogBus.Warn(nameof(HalconGlobalHelper), $"Halcon Pose长度异常，当前:{hPose?.Length ?? 0}，需=6");
                return new Pose3D(0, 0, 0, 0, 0, 0);
            }

            return new Pose3D(
                hPose[0].D,
                hPose[1].D,
                hPose[2].D,
                hPose[3].D,
                hPose[4].D,
                hPose[5].D
            );
        }

        public static HTuple Pose3DToHtuplePose(Pose3D pose)
        {
            return new HTuple(pose.X, pose.Y, pose.Z, pose.Rx, pose.Ry, pose.Rz);
        }
        #endregion

        #region 像素 ↔ 世界坐标（标准AffineTransPoint2d优先，自动降级手动矩阵）
        /// <summary>
        /// 像素坐标转物理毫米坐标
        /// 优先使用全版本通用AffineTransPoint2d；API不存在则降级手动矩阵计算
        /// </summary>
        public static (double worldX, double worldY) PixelToWorldMm(double pixelX, double pixelY, HTuple calibHomMat2D)
        {
            if (calibHomMat2D == null || calibHomMat2D.Length != 9)
            {
                LogBus.Error(nameof(HalconGlobalHelper), $"标定矩阵非法，要求长度9，实际:{calibHomMat2D?.Length ?? 0}");
                return (0, 0);
            }

            try
            {
                // 标准通用算子：参数顺序HomMat, Row(Y), Column(X), 输出X,Y
                HOperatorSet.AffineTransPoint2d(calibHomMat2D, pixelY, pixelX, out HTuple wx, out HTuple wy);
                return (wx.D, wy.D);
            }
            catch (MissingMethodException)
            {
                LogBus.Warn(nameof(HalconGlobalHelper), "当前Halcon版本无AffineTransPoint2d，降级手动矩阵计算");
                return CalcPixelToWorldByMatrix(pixelX, pixelY, calibHomMat2D);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(HalconGlobalHelper), $"像素转世界异常 PixX:{pixelX:F2},PixY:{pixelY:F2}", ex);
                return CalcPixelToWorldByMatrix(pixelX, pixelY, calibHomMat2D);
            }
        }

        /// <summary>
        /// 世界毫米转回像素坐标
        /// 流程：HomMat2dInvert求逆矩阵 → AffineTransPoint2d正向换算
        /// </summary>
        public static (double pixelX, double pixelY) WorldMmToPixel(double worldX, double worldY, HTuple calibHomMat2D)
        {
            if (calibHomMat2D == null || calibHomMat2D.Length != 9)
            {
                LogBus.Error(nameof(HalconGlobalHelper), $"标定矩阵非法，要求长度9，实际:{calibHomMat2D?.Length ?? 0}");
                return (0, 0);
            }

            try
            {
                // 仿射反向：矩阵求逆，再用正向算子
                HOperatorSet.HomMat2dInvert(calibHomMat2D, out HTuple matInv);
                HOperatorSet.AffineTransPoint2d(matInv, worldY, worldX, out HTuple px, out HTuple py);
                return (px.D, py.D);
            }
            catch (MissingMethodException)
            {
                LogBus.Warn(nameof(HalconGlobalHelper), "无AffineTransPoint2d，降级手动逆矩阵计算");
                return CalcWorldToPixelByMatrix(worldX, worldY, calibHomMat2D);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(HalconGlobalHelper), $"世界转像素异常 WX:{worldX:F2},WY:{worldY:F2}", ex);
                return CalcWorldToPixelByMatrix(worldX, worldY, calibHomMat2D);
            }
        }

        #region 兜底手动矩阵计算（无API时备用）
        private static (double worldX, double worldY) CalcPixelToWorldByMatrix(double pixelX, double pixelY, HTuple mat)
        {
            double h00 = mat[0].D;
            double h01 = mat[1].D;
            double h02 = mat[2].D;
            double h10 = mat[3].D;
            double h11 = mat[4].D;
            double h12 = mat[5].D;

            double wx = h00 * pixelX + h01 * pixelY + h02;
            double wy = h10 * pixelX + h11 * pixelY + h12;
            return (wx, wy);
        }

        private static (double pixelX, double pixelY) CalcWorldToPixelByMatrix(double worldX, double worldY, HTuple mat)
        {
            HOperatorSet.HomMat2dInvert(mat, out HTuple invMat);
            double h00 = invMat[0].D;
            double h01 = invMat[1].D;
            double h02 = invMat[2].D;
            double h10 = invMat[3].D;
            double h11 = invMat[4].D;
            double h12 = invMat[5].D;

            double px = h00 * worldX + h01 * worldY + h02;
            double py = h10 * worldX + h11 * worldY + h12;
            return (px, py);
        }
        #endregion
        #endregion

        #region HWindow绘制十字
        public static void DrawCross(HWindow window, double x, double y, double crossLen = DefaultCrossLength, string color = DefaultDrawColor)
        {
            // C#7.3不支持属性模式匹配 is not { IsInitialized:true }，改为传统判断
            if (window == null || window.IsInitialized() == false)
            {
                return;
            }
            try
            {
                window.SetColor(color);
                // DispLine参数顺序：Row1,Col1,Row2,Col2 对应 Y1,X1,Y2,X2
                window.DispLine(y - crossLen, x, y + crossLen, x);
                window.DispLine(y, x - crossLen, y, x + crossLen);
            }
            catch (Exception ex)
            {
                LogBus.Warn(nameof(HalconGlobalHelper), $"绘制十字失败 X:{x:F2} Y:{y:F2},{ex.Message}");
            }
        }
        #endregion
    }
}