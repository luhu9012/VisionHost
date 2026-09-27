//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CrossCameraPairSample.cs
// 说 明: 上下机映射（跨相机映射）的【一对】样本 = 同一个 Mark 先后被上相机与下相机观测到的像素。
//
// 为什么要成对：跨相机映射的右边是"另一台相机的像素平面"，"同一个物理点在两台相机里各占哪个像素"
//   只能靠同一 Mark 被两台相机先后看到来建立对应。所以样本天生是**成对**的，
//   不能拆成两个独立点列再对齐（那样谁跟谁配？）。
//
// ★这两个坐标都是【像素】，不是毫米 ⇒ 不复用 CalibrationPointModel 的 PixelX/WorldX 字段
//   （把"另一台相机的像素"塞进 WorldX 就是"同名两义"——ToolOffsetWx/Wy 那次踩过）。
//===================================================================================
namespace Grayson.Vision.Contracts.Calibration.Models
{
    /// <summary>一对跨相机像素样本：同一 Mark 在上相机里的像素与在下相机里的像素。</summary>
    public class CrossCameraPairSample
    {
        /// <summary>序号（从 1 起，界面显示用）</summary>
        public int Index { get; set; }

        /// <summary>上相机像素 X</summary>
        public double UpX { get; set; }
        /// <summary>上相机像素 Y</summary>
        public double UpY { get; set; }
        /// <summary>下相机像素 X</summary>
        public double DownX { get; set; }
        /// <summary>下相机像素 Y</summary>
        public double DownY { get; set; }

        /// <summary>备忘（现场可写"第几次走位"之类；不参与拟合）</summary>
        public string Note { get; set; }
    }
}
