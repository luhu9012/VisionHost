using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

// 位置：Grayson.Vision.Contracts/Business/Models/NodeRenderData.cs
namespace Grayson.Vision.Contracts.Business.Models
{
    /// <summary>
    /// 节点渲染数据（UI无关、Halcon无关，纯数据/指针契约）
    /// </summary>
    public class NodeRenderData
    {
        public string NodeId { get; set; }
        public string NodeName { get; set; }

        /// <summary>
        /// 图像句柄指针（IntPtr / HImage.Key），UI层拿到后自行封装为 HImage/Bitmap
        /// </summary>
        public object ImageObject { get; set; }

        /// <summary>
        /// 区域/ROI数据列表（可以是 HRegion 句柄或 JSON 坐标数据）
        /// </summary>
        public List<object> RegionObjects { get; set; } = new List<object>();

        /// <summary>
        /// 文本/ROI信息
        /// </summary>
        public List<TextOverlayData> TextOverlays { get; set; } = new List<TextOverlayData>();
    }

    public class TextOverlayData
    {
        public string Text { get; set; }
        public double Row { get; set; }
        public double Column { get; set; }
        public string Color { get; set; } = "green";
    }
}
