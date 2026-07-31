namespace Grayson.Vision.Contracts.Core
{
    /// <summary>
    /// VisionContext.SharedData 所有Key常量统一管理
    /// 所有业务单元读写共享数据必须使用此处常量，禁止手写字符串
    /// 便于全项目统一维护、编辑器校验、文档梳理
    /// </summary>
    public static class ContextDataKeys
    {
        #region 图像采集相关
        /// <summary>本次相机采集原始图像HObject</summary>
        public const string Grab_SourceImage = "Grab.SourceImage";
        /// <summary>相机触发时间戳</summary>
        public const string Grab_TriggerTime = "Grab.TriggerTimestamp";
        #endregion

        #region 定位匹配相关
        /// <summary>匹配是否成功 bool</summary>
        public const string Match_IsSuccess = "Match.IsSuccess";
        /// <summary>工件定位位姿 Pose3D</summary>
        public const string Match_WorkPose = "Match.Pose3D";
        #endregion

        #region 尺寸测量
        /// <summary>尺寸测量结果集合</summary>
        public const string Measure_DimensionResult = "Measure.DimensionData";
        #endregion

        #region 缺陷检测
        /// <summary>缺陷总数量 int</summary>
        public const string Defect_Count = "Defect.TotalCount";
        /// <summary>缺陷明细列表</summary>
        public const string Defect_List = "Defect.ItemList";
        #endregion

        #region 机器人运动
        /// <summary>机器人目标移动位姿 Pose3D</summary>
        public const string Robot_TargetPose = "Robot.TargetPose";
        #endregion

        #region PLC交互
        /// <summary>PLC输入信号集合</summary>
        public const string Plc_InputSignal = "Plc.InputSignal";
        /// <summary>PLC待写入输出数据</summary>
        public const string Plc_OutputSignal = "Plc.OutputSignal";
        #endregion
    }
}