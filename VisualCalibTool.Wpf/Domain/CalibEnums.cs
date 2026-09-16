namespace VisualCalibTool.Domain
{
    /// <summary>
    /// 标定链种类。★ 这是与主项目共享的消费语义口径（见设计文档 §6 与 CalibrationGeometry 唯一真源）。
    /// </summary>
    public enum CalibChainKind
    {
        /// <summary>H —— 九点标定：像素 → 世界（法兰命令位域）。产出 .tup。</summary>
        NinePoint = 0,

        /// <summary>e —— 吸嘴偏心：真吸嘴相对旋转中心的偏移。依赖旋转中心 O。</summary>
        ToolOffset = 1,

        /// <summary>t —— 工具旋转：工具尖的旋转标定（间接对针 / 图像法）。</summary>
        ToolRotation = 2,

        /// <summary>O —— 旋转中心（映射域定圆，半径丢弃）。e 的前置。</summary>
        RotationCenter = 3,

        /// <summary>相机内参 + 镜头畸变（本工具新增能力，生产链暂不消费）。</summary>
        Intrinsics = 4
    }

    /// <summary>标定任务的运行状态。</summary>
    public enum CalibRunState
    {
        Idle = 0,
        Running = 1,
        Paused = 2,
        Succeeded = 3,
        Failed = 4,
        Cancelled = 5
    }

    /// <summary>单个采样点的状态（每一步失败都要能"卡哪说哪"）。</summary>
    public enum CalibSampleState
    {
        Pending = 0,
        Moving = 1,
        Settling = 2,
        Grabbing = 3,
        Extracting = 4,
        Ok = 5,
        Failed = 6,
        Rejected = 7
    }

    /// <summary>
    /// 标定特征种类。★ 必须与主项目 <c>CalibrationFeatureType</c> 的能力对等，三类全做，否则是能力倒退。
    /// </summary>
    public enum FeatureKind
    {
        /// <summary>圆 Mark：阈值分割 + 圆度筛选 + 亚像素圆拟合（含参考半径 ±40% 滤伪）。</summary>
        CircleMark = 0,

        /// <summary>十字 Mark：骨架 + 直线交叉点。</summary>
        CrossMark = 1,

        /// <summary>模板匹配：Shape / NCC（模板库由本工具自建，嵌入模式下可选注入宿主模板库）。</summary>
        TemplateMatch = 2
    }

    /// <summary>相机安装方式（决定是否叠加旋转中心补偿 O）。</summary>
    public enum CameraMountKind
    {
        /// <summary>眼在手：相机随机械手移动 → 需要 O 补偿。</summary>
        EyeInHand = 0,

        /// <summary>眼在手外（固定相机）：直吸时不需要 O 补偿；延伸杆辅助标定时需要。</summary>
        EyeToHand = 1,

        /// <summary>未知：按最保守方式处理并告警。</summary>
        Unknown = 2
    }

    /// <summary>机械手手系。★ Epson 的 /L /R 是手系而非"不等待"，必须显式下发。</summary>
    public enum Handedness
    {
        Unknown = 0,
        Lefty = 1,
        Righty = 2
    }

    /// <summary>标定板类型（内参/畸变标定用，板模型可插拔）。</summary>
    public enum BoardKind
    {
        /// <summary>HALCON 标准标定板（MVTec 官方 PDF，识别链最稳，默认）。</summary>
        HalconCalplate = 0,

        /// <summary>棋盘格（工厂常见，find_chessboard_corners 链）。</summary>
        Chessboard = 1
    }

    /// <summary>向导步骤状态。</summary>
    public enum WizardStepState
    {
        Pending = 0,
        Running = 1,
        Ok = 2,
        Failed = 3,
        Skipped = 4
    }

    /// <summary>
    /// 偏心（t）的标定方法。
    /// ★ 主项目口径：<c>LegacyUnknown(0)</c> 在 EyeToHand 下<b>视为过期</b>（必须重标），
    ///   因为在固定相机下"工具尖在哪"没法靠老数据外推。
    /// </summary>
    public enum ToolOffsetMethod
    {
        /// <summary>未知 / 历史遗留（ETH 下视为过期，需要重标）。</summary>
        LegacyUnknown = 0,

        /// <summary>EyeInHand 间接对针：把工具尖对到固定的针/基准上，走位求取。</summary>
        EyeInHandIndirect = 1,

        /// <summary>EyeToHand 图像法：固定相机直接拍工具尖与特征，图像上量偏心。</summary>
        EyeToHandImage = 2
    }

    /// <summary>标定产物的生命周期（与主项目 CalibrationArtifact 同构）。</summary>
    public enum CalibArtifactState
    {
        Draft = 0,
        SampleComplete = 1,
        Verified = 2,
        Published = 3,
        Expired = 4
    }

    /// <summary>统一的失败分类，便于"毫米级原因 + 一键重采"。</summary>
    public enum CalibFailureKind
    {
        None = 0,

        /// <summary>运动被控制器拒绝（保留原始错误码，如 4001 / 4007 / 2997）。</summary>
        MotionRejected = 1,

        /// <summary>目标点不可达（零运动校核 CHECK 返回 NG）。</summary>
        Unreachable = 2,

        /// <summary>取图超时。</summary>
        GrabTimeout = 3,

        /// <summary>特征提取失败 / 置信度过低。</summary>
        FeatureNotFound = 4,

        /// <summary>几何解算失败（点数不足、矩阵奇异等）。</summary>
        SolveFailed = 5,

        /// <summary>自检未通过（σ1/σ2 超限、LOO 超限、镜像等）。</summary>
        QualityGate = 6,

        /// <summary>被安全守卫拦截（软限位 / Z 范围 / 可达域）。</summary>
        SafetyBlocked = 7,

        /// <summary>用户取消。</summary>
        UserCancelled = 8,

        /// <summary>内部异常。</summary>
        Internal = 9
    }
}
