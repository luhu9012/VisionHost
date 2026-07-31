using HalconDotNet;
using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace Grayson.Vison.FlowEdit.Model
{

    public class HalconRenderContext
    {
        public string NodeId { get; set; }
        public string NodeName { get; set; }
        public HImage Image { get; set; }
        public BitmapSource Thumbnail { get; set; } // 缩略图（WPF用）
        public List<HObject> Regions { get; set; } = new List<HObject>();
        public List<HalconTextOverlay> Texts { get; set; } = new List<HalconTextOverlay>();
        public bool IsSelected { get; set; }
    }

    public class HalconTextOverlay
    {
        public string Text { get; set; }
        public double Row { get; set; }
        public double Column { get; set; }
        public string Color { get; set; } = "green";
    }
}

