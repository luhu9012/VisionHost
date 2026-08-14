using System;
using Grayson.Vision.Common.Logging;
using Grayson.Vision.Contracts.Core;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Calibration
{
    public class FixtureData
    {
        public double DeltaRow { get; set; }
        public double DeltaCol { get; set; }
        public double DeltaAngle { get; set; }
        public object HomMat2DHandle { get; set; }
    }

    public static class FixtureTool
    {
        /// <summary>建立位置补正矩阵</summary>
        public static Result<FixtureData> CreateFixture(double refRow, double refCol, double refAngle, double curRow, double curCol, double curAngle)
        {
            try
            {
                HTuple homMat2D;
                HOperatorSet.VectorAngleToRigid(
                    refRow, refCol, refAngle * Math.PI / 180.0,
                    curRow, curCol, curAngle * Math.PI / 180.0,
                    out homMat2D);

                var fixture = new FixtureData
                {
                    DeltaRow = curRow - refRow,
                    DeltaCol = curCol - refCol,
                    DeltaAngle = curAngle - refAngle,
                    HomMat2DHandle = homMat2D
                };

                return Result<FixtureData>.Ok(fixture);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("建立位置补正矩阵失败", ex, nameof(FixtureTool));
                return Result<FixtureData>.Fail("建立位置补正失败: " + ex.Message);
            }
        }

        /// <summary>应用位置跟随：映射点坐标</summary>
        public static Result<(double newRow, double newCol)> ApplyFixtureToPoint(double baseRow, double baseCol, object homMat2DHandle)
        {
            var mat = homMat2DHandle as HTuple;
            if (mat == null)
                return Result<(double, double)>.Fail("无效的补正矩阵句柄");

            try
            {
                HOperatorSet.AffineTransPoint2d(mat, baseRow, baseCol, out HTuple resRow, out HTuple resCol);
                return Result<(double, double)>.Ok((resRow.D, resCol.D));
            }
            catch (Exception ex)
            {
                return Result<(double, double)>.Fail("坐标跟随变换失败: " + ex.Message);
            }
        }

        /// <summary>应用位置跟随：映射 Region 区域</summary>
        public static Result<object> ApplyFixtureToRegion(object baseRegion, object homMat2DHandle)
        {
            var region = baseRegion as HObject;
            var mat = homMat2DHandle as HTuple;

            if (region == null || !region.IsInitialized() || mat == null)
                return Result<object>.Fail("输入的 Region 或补正矩阵句柄无效");

            try
            {
                HObject resRegion;
                HOperatorSet.AffineTransRegion(region, out resRegion, mat, "nearest_neighbor");
                return Result<object>.Ok(resRegion);
            }
            catch (Exception ex)
            {
                return Result<object>.Fail("区域位置跟随失败: " + ex.Message);
            }
        }
    }
}