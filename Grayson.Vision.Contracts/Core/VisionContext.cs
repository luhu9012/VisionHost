using System.Collections.Generic;
using HalconDotNet;

namespace Grayson.Vision.Contracts.Core
{
    /// <summary>
    /// 单次工位触发流程的运行上下文
    /// 规则：每次相机触发新建独立实例，流程结束必须调用CleanAllResource释放资源
    /// 禁止全局单例复用，防止多工位数据串扰、内存堆积
    /// </summary>
    public class VisionContext
    {
        /// <summary>本次检测原始图像，单次流程唯一主图</summary>
        public HObject SourceImage { get; set; }

        /// <summary>工件定位输出的基准位姿</summary>
        public Pose3D WorkpiecePose { get; set; }

        /// <summary>跨单元共享数据字典，所有临时变量存放位置</summary>
        public Dictionary<string, object> SharedData { get; set; } = new Dictionary<string, object>();

        /// <summary>本次触发Unix时间戳</summary>
        public long TriggerTimestamp { get; set; }

        /// <summary>
        /// 流程执行完毕强制资源清理
        /// 释放Halcon图像、清空临时字典，杜绝内存泄漏
        /// </summary>
        public void CleanAllResource()
        {
            // 释放Halcon图像资源
            if (SourceImage != null)
            {
                SourceImage.Dispose();
                SourceImage = null;
            }
            // 清空所有临时业务变量
            SharedData.Clear();
        }
    }
}