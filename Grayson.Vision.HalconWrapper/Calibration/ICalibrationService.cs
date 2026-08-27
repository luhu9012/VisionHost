using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Imaging;
using System.Collections.Generic;
using Grayson.Vision.Contracts.Calibration.Models;

namespace Grayson.Vision.HalconWrapper.Calibration
{
    public class CalibrationResult
    {
        public string SavedFilePath { get; set; }
        public double RmsError { get; set; }
    }

    public class DetectionResult
    {
        public double[] Px { get; set; }
        public double[] Py { get; set; }
    }

    public interface ICalibrationService
    {
        /// <summary>
        /// 标定显示上下文（Halcon 视图窗口句柄）。
        /// 非空时，ExtractFeaturePoint 提取特征过程中会直接把 ROI 搜索框、候选区域、
        /// 亚像素轮廓、拟合圆、特征点十字、文字标注绘制到图像显示窗口（句柄模式，
        /// 由 CalibrationService 用 Halcon 原生算子精细控制显示）。
        /// </summary>
        ICalibrationDisplayContext DisplayContext { get; set; }

        /// <summary>
        /// 获取系统中所有标定方案配置
        /// </summary>
        Result<List<CalibrationProfile>> GetAllProfiles();
        /// <summary>
        /// 九点标定计算HomMat2D单应矩阵
        /// </summary>
        Result<CalibrationResult> CalcNinePointHomMat(double[] pixelXList, double[] pixelYList, double[] worldXList, double[] worldYList);

        /// <summary>
        /// 保存标定矩阵文件
        /// </summary>
        Result SaveHomMatFile(string sourceFilePath, string destFilePath);

        /// <summary>
        /// 标定板角点/圆点检测
        /// </summary>
        Result<DetectionResult> DetectCalibrationPoints(string imageFilePath);

        /// <summary>
        /// 建立位置补正
        /// </summary>
        Result<FixtureData> CreateFixture(double refRow, double refCol, double refAngle, double curRow, double curCol, double curAngle);

        /// <summary>
        /// 像素坐标转换物理坐标 (Pixel -> World)
        /// </summary>
        Result<(double WorldX, double WorldY)> MapPixelToWorld(string matrixFilePath, double px, double py);

        /// <summary>
        /// 物理坐标逆转换像素坐标 (World -> Pixel)
        /// </summary>
        Result<(double PixelX, double PixelY)> MapWorldToPixel(string matrixFilePath, double wx, double wy);
        /// <summary>
        /// 高级手眼坐标转换：考虑像素、物理旋转中心与角度补正
        /// </summary>
        Result<(double FinalWorldX, double FinalWorldY)> MapPixelToWorldWithOffset(
           string matrixFilePath,
           double px, double py,
           double rotateAngleDeg,
           double centerWx, double centerWy,
           EyeMode eyeMode,
           (double RobotX, double RobotY) currentRobotPos);


        /// <summary>
        /// 从图像句柄/渲染上下文中提取标定特征标记点（圆心/十字中心/角点）像素坐标
        /// </summary>
        /// <param name="imageHandle">Halcon HObject图像句柄或WpfImageRenderContext中的Image对象</param>
        /// <param name="pointIndex">当前标定点索引（1~9）</param>
        /// <param name="expectedPx">预期/参考像素X（用于局部ROI搜索）</param>
        /// <param name="expectedPy">预期/参考像素Y（用于局部ROI搜索）</param>
        /// <param name="forceFullImage">true=跳过 ROI 裁剪做全图搜索，但保留 expected 用于多候选择近（降级重试用）</param>
        Result<(double PixelX, double PixelY)> ExtractFeaturePoint(object imageHandle, int pointIndex, double expectedPx, double expectedPy, bool forceFullImage = false);

        /// <summary>
        /// 按标定方案配置的特征类型路由提取算法（九点标定采样 / 旋转采样统一入口）：
        /// CircleMark → 圆度筛选+亚像素圆拟合；CrossMark → 骨架+直线交叉。
        /// 保证第三步采样与第二步特征预览使用同一套算法。
        /// </summary>
        /// <param name="imageHandle">Halcon HObject/HImage 或 IRenderImage 包装</param>
        /// <param name="featureType">特征类型：CircleMark / CrossMark</param>
        /// <param name="pointIndex">当前标定点索引（预览传 0）</param>
        /// <param name="expectedPx">预期/参考像素X（用于局部ROI搜索，&lt;=0 全图搜索）</param>
        /// <param name="expectedPy">预期/参考像素Y（用于局部ROI搜索，&lt;=0 全图搜索）</param>
        /// <param name="forceFullImage">true=跳过 ROI 裁剪做全图搜索，但保留 expected 用于多候选择近（降级重试用）</param>
        Result<(double PixelX, double PixelY)> ExtractFeaturePointByType(object imageHandle, CalibrationFeatureType featureType, int pointIndex, double expectedPx, double expectedPy, bool forceFullImage = false);

        /// <summary>
        /// 【已采集点标记叠加】把所有已成功采集的标定点以紧凑绿十字标记阵列追加绘制到
        /// 当前显示场景（不清屏，叠加在刚完成的算子过程结果之上）。
        /// 九点标定逐点采样时每采完一点调用：识别正确时十字阵列与走位网格一致（规则 3x3），
        /// 误检点表现为十字重叠/缺失/阵列畸变，操作员目视即可发现哪个像素点取错。
        /// </summary>
        /// <param name="pointIndices">已采集点编号（1~9）</param>
        /// <param name="pixelXs">已采集点像素 X（列）</param>
        /// <param name="pixelYs">已采集点像素 Y（行）</param>
        void AppendCapturedMarks(int[] pointIndices, double[] pixelXs, double[] pixelYs);

        /// <summary>
        /// 【参考 Mark 重置】清除已记录的参考 Mark 半径（圆 Mark 伪特征过滤依据）。
        /// 重新进入标定步骤、更换 Mark 板或更换相机后调用；参考半径由首个成功识别的
        /// 圆 Mark 自动记录（含第二步特征预览），此后多候选时按半径±40%排除伪特征。
        /// </summary>
        void ResetMarkReference();

        /// <summary>
        /// 从旋转图像句柄中提取旋转标记点像素坐标
        /// </summary>
        /// <param name="imageHandle">Halcon图像对象</param>
        /// <param name="angleDeg">当前旋转角度</param>
        /// <param name="featureType">标定方案配置的特征类型（圆 / 十字）</param>
        /// <param name="expectedPx">预期/参考中心像素X</param>
        /// <param name="expectedPy">预期/参考中心像素Y</param>
        Result<(double PixelX, double PixelY)> ExtractRotationFeaturePoint(object imageHandle, double angleDeg, CalibrationFeatureType featureType, double expectedPx, double expectedPy);

        /// <summary>
        /// 特征提取参数（阈值 / 圆度 / 面积 / 搜索半径等）。
        /// 操作员在标定向导第二步通过滑块实时调节，作用于
        /// ExtractFeaturePoint / ExtractRotationFeaturePoint / ExtractFeaturePreview。
        /// </summary>
        FeatureExtractOptions ExtractOptions { get; }

        /// <summary>
        /// 特征预览提取（第二步"特征配置"验证用）：按用户选择的特征类型，
        /// 在当前图像上提取特征中心，并把识别过程与结果（底图 / ROI 搜索框 / 候选区域 /
        /// 拟合圆或拟合直线 / 中心十字 / 文字标注）通过 DisplayContext 叠加绘制到视图窗口。
        /// 圆形 Mark 走阈值分割+圆度筛选+亚像素圆拟合；十字 Mark 走骨架+直线交叉点。
        /// 不改变标定流程，仅用于操作员实时确认识别效果。
        /// </summary>
        /// <param name="imageHandle">Halcon HObject/HImage 或 IRenderImage 包装（取 NativeHandle）</param>
        /// <param name="featureType">特征类型：CircleMark / CrossMark</param>
        Result<(double PixelX, double PixelY)> ExtractFeaturePreview(object imageHandle, CalibrationFeatureType featureType);

    }
}