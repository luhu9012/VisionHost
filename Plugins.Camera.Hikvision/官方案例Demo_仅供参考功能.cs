#region 程序集引用说明
// MvCamCtrl.Net：海康工业相机C#托管SDK，封装全部原生C接口
// 原生底层依赖zlgdigicam.dll等C库，本Demo依靠托管层调用硬件
#endregion
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using MvCamCtrl.NET;
using MvCamCtrl.NET.CameraParams;
using System.Runtime.InteropServices;
using System.Threading;
using System.IO;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Collections.ObjectModel;

//程序启动 → 枚举局域网 / USB所有海康相机 → 下拉框展示设备
//↓
//选择相机 → CreateHandle创建相机句柄 → OpenDevice打开硬件连接
//↓
//GigE相机自动适配最优网络包大小，防止丢包
//↓
//配置曝光、增益、帧率、触发模式（连续采集 / 硬触发 / 软触发）
//↓
//StartGrab开启相机硬件输出图像流，开启独立后台采集线程
//↓
//子线程死循环主动调用GetImageBuffer拉取SDK缓存图像
//↓
//像素格式转换（Bayer转RGB、Mono灰度适配Bitmap）
//↓ 双路线：①pictureBox原生渲染预览 ②拷贝到全局Bitmap供业务 / 保存
//↓
//停止采集：终止线程 → StopGrab → CloseDevice销毁句柄释放硬件
//↓
//窗口关闭强制兜底释放所有资源

namespace BasicDemo
{
    public partial class Form1 : Form
    {
        #region 全局硬件&缓存核心变量
        /// <summary>海康相机顶层实例，封装所有硬件操作，持有设备句柄</summary>
        private CCamera m_MyCamera = new CCamera();
        /// <summary>枚举出来的全部硬件设备列表：存储GigE/USB相机原生结构体</summary>
        List<CCameraInfo> m_ltDeviceList = new List<CCameraInfo>();

        /// <summary>采集启停标记：true=正在取图，后台线程循环运行</summary>
        bool m_bGrabbing = false;
        /// <summary>独立采集子线程：专门负责循环拉取图像，绝对不能在UI主线程取流（卡顿卡死）</summary>
        Thread m_hReceiveThread = null;

        /// <summary>多线程锁：图像资源跨线程读写加锁，防止UI线程、采集线程同时篡改内存图像导致花屏崩溃</summary>
        private static Object BufForDriverLock = new Object();
        /// <summary>SDK原生图像缓存：存放SDK返回的裸图像数据（宽高、像素字节、元信息）</summary>
        CImage m_pcImgForDriver;
        /// <summary>帧附加信息：帧号、时间戳、水印等附属信息</summary>
        CFrameSpecInfo m_pcImgSpecInfo;

        /// <summary>最终给WinForms显示、保存用的标准GDI Bitmap</summary>
        Bitmap m_pcBitmap = null;
        /// <summary>当前图像Bitmap像素格式：灰度8bpp / 彩色24bppRGB</summary>
        PixelFormat m_enBitmapPixelFormat = PixelFormat.DontCare;
        #endregion

        public Form1()
        {
            InitializeComponent();
            // 窗体加载立刻枚举所有相机
            DeviceListAcq();
            // 禁止跨线程校验：本Demo偷懒写法，工业正式项目严禁使用，正规要用Dispatcher/Invoke跨线程UI
            Control.CheckForIllegalCrossThreadCalls = false;
        }

        #region 通用工具：错误码翻译打印
        /// <summary>把海康数字错误码翻译成可读文字弹窗提示</summary>
        private void ShowErrorMsg(string csMessage, int nErrorNum)
        {
            string errorMsg;
            if (nErrorNum == 0)
            {
                // 返回0=MV_OK无错误
                errorMsg = csMessage;
            }
            else
            {
                // 16进制打印错误码，海康SDK习惯十六进制排查问题
                errorMsg = csMessage + ": Error =" + String.Format("{0:X}", nErrorNum);
            }

            // 海康全量错误码中文释义，来自官方CErrorDefine枚举
            switch (nErrorNum)
            {
                case CErrorDefine.MV_E_HANDLE: errorMsg += " 句柄错误/设备未创建"; break;
                case CErrorDefine.MV_E_SUPPORT: errorMsg += " 当前相机不支持该功能"; break;
                case CErrorDefine.MV_E_BUFOVER: errorMsg += " SDK图像缓存溢出，帧率过高处理不过来"; break;
                case CErrorDefine.MV_E_CALLORDER: errorMsg += " API调用顺序错误（比如没Open就StartGrab）"; break;
                case CErrorDefine.MV_E_PARAMETER: errorMsg += " 入参非法（曝光填负数、地址不存在）"; break;
                case CErrorDefine.MV_E_RESOURCE: errorMsg += " 申请硬件/内存资源失败"; break;
                case CErrorDefine.MV_E_NODATA: errorMsg += " 超时未收到图像，硬件无数据返回"; break;
                case CErrorDefine.MV_E_PRECONDITION: errorMsg += " 前置条件不满足，环境变动"; break;
                case CErrorDefine.MV_E_VERSION: errorMsg += " SDK版本和相机固件不匹配"; break;
                case CErrorDefine.MV_E_NOENOUGH_BUF: errorMsg += " 内存不足，存不下一帧图像"; break;
                case CErrorDefine.MV_E_UNKNOW: errorMsg += " 未知底层错误"; break;
                case CErrorDefine.MV_E_GC_GENERIC: errorMsg += " GenICam通用底层错误"; break;
                case CErrorDefine.MV_E_GC_ACCESS: errorMsg += " GenICam节点读写权限错误"; break;
                case CErrorDefine.MV_E_ACCESS_DENIED: errorMsg += " 无权限占用相机，别的软件正在占用"; break;
                case CErrorDefine.MV_E_BUSY: errorMsg += " 设备忙/网线断开/USB掉线"; break;
                case CErrorDefine.MV_E_NETER: errorMsg += " 网络异常（GigE相机网线松动、IP不通）"; break;
            }

            MessageBox.Show(errorMsg, "PROMPT");
        }
        #endregion

        #region 像素格式判断工具（区分灰度/彩色，用于Bitmap格式适配）
        /// <summary>判断像素是否为单色灰度（Mono系列）</summary>
        private Boolean IsMonoData(MvGvspPixelType enGvspPixelType)
        {
            switch (enGvspPixelType)
            {
                case MvGvspPixelType.PixelType_Gvsp_Mono8:
                case MvGvspPixelType.PixelType_Gvsp_Mono10:
                case MvGvspPixelType.PixelType_Gvsp_Mono10_Packed:
                case MvGvspPixelType.PixelType_Gvsp_Mono12:
                case MvGvspPixelType.PixelType_Gvsp_Mono12_Packed:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>判断是否为彩色Bayer格式（拜耳原始马赛克，必须转RGB才能正常显示）</summary>
        private Boolean IsColorData(MvGvspPixelType enGvspPixelType)
        {
            switch (enGvspPixelType)
            {
                // 各类Bayer拜耳原始格式、RGB、YUV全部归类彩色
                case MvGvspPixelType.PixelType_Gvsp_BayerGR8:
                case MvGvspPixelType.PixelType_Gvsp_BayerRG8:
                case MvGvspPixelType.PixelType_Gvsp_BayerGB8:
                case MvGvspPixelType.PixelType_Gvsp_BayerBG8:
                case MvGvspPixelType.PixelType_Gvsp_BayerGR10:
                case MvGvspPixelType.PixelType_Gvsp_BayerRG10:
                case MvGvspPixelType.PixelType_Gvsp_BayerGB10:
                case MvGvspPixelType.PixelType_Gvsp_BayerBG10:
                case MvGvspPixelType.PixelType_Gvsp_BayerGR12:
                case MvGvspPixelType.PixelType_Gvsp_BayerRG12:
                case MvGvspPixelType.PixelType_Gvsp_BayerGB12:
                case MvGvspPixelType.PixelType_Gvsp_BayerBG12:
                case MvGvspPixelType.PixelType_Gvsp_BayerGR10_Packed:
                case MvGvspPixelType.PixelType_Gvsp_BayerRG10_Packed:
                case MvGvspPixelType.PixelType_Gvsp_BayerGB10_Packed:
                case MvGvspPixelType.PixelType_Gvsp_BayerBG10_Packed:
                case MvGvspPixelType.PixelType_Gvsp_BayerGR12_Packed:
                case MvGvspPixelType.PixelType_Gvsp_BayerRG12_Packed:
                case MvGvspPixelType.PixelType_Gvsp_BayerGB12_Packed:
                case MvGvspPixelType.PixelType_Gvsp_BayerBG12_Packed:
                case MvGvspPixelType.PixelType_Gvsp_RGB8_Packed:
                case MvGvspPixelType.PixelType_Gvsp_YUV422_Packed:
                case MvGvspPixelType.PixelType_Gvsp_YUV422_YUYV_Packed:
                    return true;
                default:
                    return false;
            }
        }
        #endregion

        #region 【阶段1：设备枚举】扫描局域网+USB所有海康相机
        /// <summary>枚举按钮点击：重新刷新全部相机列表</summary>
        private void bnEnum_Click(object sender, EventArgs e)
        {
            DeviceListAcq();
        }

        /// <summary>核心枚举函数：调用SDK全局接口CSystem.EnumDevices扫描硬件</summary>
        private void DeviceListAcq()
        {
            // 主动GC回收内存，避免旧缓存干扰枚举
            System.GC.Collect();
            cbDeviceList.Items.Clear();
            m_ltDeviceList.Clear();

            // SDK全局枚举：同时扫描GigE网口相机 + USB3.0工业相机
            int nRet = CSystem.EnumDevices(CSystem.MV_GIGE_DEVICE | CSystem.MV_USB_DEVICE, ref m_ltDeviceList);
            if (0 != nRet)
            {
                ShowErrorMsg("Enumerate devices fail!", 0);
                return;
            }

            // 遍历所有设备，区分GigE/USB，格式化显示到下拉框
            for (int i = 0; i < m_ltDeviceList.Count; i++)
            {
                CCameraInfo deviceRaw = m_ltDeviceList[i];
                // GigE网口相机强转CGigECameraInfo，读取IP、自定义名称、序列号
                if (deviceRaw.nTLayerType == CSystem.MV_GIGE_DEVICE)
                {
                    CGigECameraInfo gigeInfo = (CGigECameraInfo)m_ltDeviceList[i];
                    // 优先展示用户自定义相机名称，无自定义则展示厂商+型号，末尾追加序列号区分多同型号相机
                    if (gigeInfo.UserDefinedName != "")
                    {
                        cbDeviceList.Items.Add($"GEV: {gigeInfo.UserDefinedName} ({gigeInfo.chSerialNumber})");
                    }
                    else
                    {
                        cbDeviceList.Items.Add($"GEV: {gigeInfo.chManufacturerName} {gigeInfo.chModelName} ({gigeInfo.chSerialNumber})");
                    }
                }
                // USB相机强转CUSBCameraInfo
                else if (m_ltDeviceList[i].nTLayerType == CSystem.MV_USB_DEVICE)
                {
                    CUSBCameraInfo usbInfo = (CUSBCameraInfo)m_ltDeviceList[i];
                    if (usbInfo.UserDefinedName != "")
                    {
                        cbDeviceList.Items.Add($"U3V: {usbInfo.UserDefinedName} ({usbInfo.chSerialNumber})");
                    }
                    else
                    {
                        cbDeviceList.Items.Add($"U3V: {usbInfo.chManufacturerName} {usbInfo.chModelName} ({usbInfo.chSerialNumber})");
                    }
                }
            }

            // 默认选中列表第一个相机
            if (m_ltDeviceList.Count != 0)
            {
                cbDeviceList.SelectedIndex = 0;
            }
        }
        #endregion

        #region 【阶段2：打开相机硬件连接】创建句柄+Open设备
        /// <summary>打开相机按钮：根据下拉选中设备建立硬件连接</summary>
        private void bnOpen_Click(object sender, EventArgs e)
        {
            // 无设备、未选中直接返回
            if (m_ltDeviceList.Count == 0 || cbDeviceList.SelectedIndex == -1)
            {
                ShowErrorMsg("No device, please select", 0);
                return;
            }

            // 取出选中相机的原生枚举信息
            CCameraInfo device = m_ltDeviceList[cbDeviceList.SelectedIndex];

            // 实例化相机托管对象
            if (null == m_MyCamera)
            {
                m_MyCamera = new CCamera();
            }

            // 1. 根据设备信息创建底层SDK设备句柄（关键：SDK靠句柄区分多台相机）
            int nRet = m_MyCamera.CreateHandle(ref device);
            if (CErrorDefine.MV_OK != nRet)
            {
                return;
            }

            // 2. 真正和相机硬件建立TCP/USB通信链路
            nRet = m_MyCamera.OpenDevice();
            if (CErrorDefine.MV_OK != nRet)
            {
                // 打开失败必须销毁句柄，防止内存泄漏、相机被占用锁死
                m_MyCamera.DestroyHandle();
                ShowErrorMsg("Device open fail!", nRet);
                return;
            }

            // GigE专属优化：自动探测网络最大传输包长，解决大分辨率图丢包、花屏
            if (device.nTLayerType == CSystem.MV_GIGE_DEVICE)
            {
                int nPacketSize = m_MyCamera.GIGE_GetOptimalPacketSize();
                if (nPacketSize > 0)
                {
                    nRet = m_MyCamera.SetIntValue("GevSCPSPacketSize", (uint)nPacketSize);
                    if (nRet != CErrorDefine.MV_OK)
                    {
                        ShowErrorMsg("Set Packet Size failed!", nRet);
                    }
                }
            }

            // 打开成功，刷新界面按钮可用状态
            SetCtrlWhenOpen();
            // 读取相机当前曝光、增益、帧率等参数回显到UI输入框
            bnGetParam_Click(null, null);
        }

        /// <summary>相机打开后UI控件状态切换：开放参数配置、关闭按钮可用</summary>
        private void SetCtrlWhenOpen()
        {
            bnOpen.Enabled = false;

            bnClose.Enabled = true;
            bnStartGrab.Enabled = true;
            bnStopGrab.Enabled = false;
            bnContinuesMode.Enabled = true;
            bnContinuesMode.Checked = true;
            bnTriggerMode.Enabled = true;
            cbSoftTrigger.Enabled = false;
            bnTriggerExec.Enabled = false;

            tbExposure.Enabled = true;
            tbGain.Enabled = true;
            tbFrameRate.Enabled = true;
            bnGetParam.Enabled = true;
            bnSetParam.Enabled = true;
            tbPixelFormat.Enabled = false;
        }
        #endregion

        #region 【阶段3：触发模式配置】连续采集 / 硬件外触发 / 软件触发切换
        /// <summary>连续采集模式勾选：关闭触发，相机自由按帧率出图</summary>
        private void bnContinuesMode_CheckedChanged(object sender, EventArgs e)
        {
            if (bnContinuesMode.Checked)
            {
                // TriggerMode=OFF 连续自由采集
                m_MyCamera.SetEnumValue("TriggerMode", (uint)MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
                cbSoftTrigger.Enabled = false;
                bnTriggerExec.Enabled = false;
            }
        }

        /// <summary>触发模式勾选：开启硬件触发模式，需要外部信号拍照</summary>
        private void bnTriggerMode_CheckedChanged(object sender, EventArgs e)
        {
            if (bnTriggerMode.Checked)
            {
                // 开启全局触发
                m_MyCamera.SetEnumValue("TriggerMode", (uint)MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_ON);

                // 触发源选择：7=软件软触发；0=Line0硬件光耦触发
                if (cbSoftTrigger.Checked)
                {
                    m_MyCamera.SetEnumValue("TriggerSource", (uint)MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE);
                    // 正在取流时，软触发按钮可以点击发命令拍照
                    if (m_bGrabbing)
                    {
                        bnTriggerExec.Enabled = true;
                    }
                }
                else
                {
                    // 默认Line0硬件输入触发（接PLC/光电传感器上升沿）
                    m_MyCamera.SetEnumValue("TriggerSource", (uint)MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0);
                }
                cbSoftTrigger.Enabled = true;
            }
        }

        /// <summary>软触发复选框切换：在Line0硬触发、软件触发之间切换</summary>
        private void cbSoftTrigger_CheckedChanged(object sender, EventArgs e)
        {
            if (cbSoftTrigger.Checked)
            {
                m_MyCamera.SetEnumValue("TriggerSource", (uint)MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE);
                if (m_bGrabbing)
                {
                    bnTriggerExec.Enabled = true;
                }
            }
            else
            {
                m_MyCamera.SetEnumValue("TriggerSource", (uint)MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0);
                bnTriggerExec.Enabled = false;
            }
        }

        /// <summary>手动发送软触发命令，命令相机拍一张图</summary>
        private void bnTriggerExec_Click(object sender, EventArgs e)
        {
            int nRet = m_MyCamera.SetCommandValue("TriggerSoftware");
            if (CErrorDefine.MV_OK != nRet)
            {
                ShowErrorMsg("Trigger Software Fail!", nRet);
            }
        }
        #endregion

        #region 【阶段4：核心采集】开启取流 + 后台子线程循环拉取图像
        /// <summary>开始采集按钮：启动硬件流、启动后台取图线程</summary>
        private void bnStartGrab_Click(object sender, EventArgs e)
        {
            // 采集前置：读取相机宽高、像素格式，初始化对应大小的Bitmap画布
            int nRet = NecessaryOperBeforeGrab();
            if (CErrorDefine.MV_OK != nRet)
            {
                return;
            }

            // 标记采集启动，子线程循环条件生效
            m_bGrabbing = true;
            // 新建后台线程执行死循环取图，不和UI线程争抢
            m_hReceiveThread = new Thread(ReceiveThreadProcess);
            m_hReceiveThread.Start();

            // 通知相机硬件开始向外输出图像数据流（GigE/U3V持续发包）
            nRet = m_MyCamera.StartGrabbing();
            if (CErrorDefine.MV_OK != nRet)
            {
                // 启动失败，终止线程、重置标记
                m_bGrabbing = false;
                m_hReceiveThread.Join();
                ShowErrorMsg("Start Grabbing Fail!", nRet);
                return;
            }

            // 更新按钮状态：停止可用、保存图片按钮解锁
            SetCtrlWhenStartGrab();
        }

        /// <summary>取流前准备：读取相机分辨率、像素格式，创建匹配尺寸的Bitmap</summary>
        private Int32 NecessaryOperBeforeGrab()
        {
            // 读取相机宽度 Width GenICam标准节点
            CIntValue pcWidth = new CIntValue();
            int nRet = m_MyCamera.GetIntValue("Width", ref pcWidth);
            if (CErrorDefine.MV_OK != nRet)
            {
                ShowErrorMsg("Get Width Info Fail!", nRet);
                return nRet;
            }
            // 读取高度 Height
            CIntValue pcHeight = new CIntValue();
            nRet = m_MyCamera.GetIntValue("Height", ref pcHeight);
            if (CErrorDefine.MV_OK != nRet)
            {
                ShowErrorMsg("Get Height Info Fail!", nRet);
                return nRet;
            }
            // 读取当前像素格式（Mono8/BayerGB8/RGB8）
            CEnumValue pcPixelFormat = new CEnumValue();
            nRet = m_MyCamera.GetEnumValue("PixelFormat", ref pcPixelFormat);
            if (CErrorDefine.MV_OK != nRet)
            {
                ShowErrorMsg("Get Pixel Format Fail!", nRet);
                return nRet;
            }

            // 根据像素格式选择GDI标准像素类型
            MvGvspPixelType pixelType = (MvGvspPixelType)pcPixelFormat.CurValue;
            if (IsMono(pixelType))
            {
                m_enBitmapPixelFormat = PixelFormat.Format8bppIndexed;
            }
            else
            {
                m_enBitmapPixelFormat = PixelFormat.Format24bppRgb;
            }

            // 销毁旧画布，新建和相机分辨率完全一致的Bitmap
            if (null != m_pcBitmap)
            {
                m_pcBitmap.Dispose();
                m_pcBitmap = null;
            }
            m_pcBitmap = new Bitmap((Int32)pcWidth.CurValue, (Int32)pcHeight.CurValue, m_enBitmapPixelFormat);

            // 灰度图必须手动设置灰度调色板，否则全黑/偏色
            if (PixelFormat.Format8bppIndexed == m_enBitmapPixelFormat)
            {
                ColorPalette palette = m_pcBitmap.Palette;
                for (int i = 0; i < palette.Entries.Length; i++)
                {
                    palette.Entries[i] = Color.FromArgb(i, i, i);
                }
                m_pcBitmap.Palette = palette;
            }

            return CErrorDefine.MV_OK;
        }

        /// <summary>【核心后台线程函数】死循环主动拉取图像、格式转换、渲染</summary>
        public void ReceiveThreadProcess()
        {
            int nRet = CErrorDefine.MV_OK;
            // 只要采集标记为true，无限循环取图
            while (m_bGrabbing)
            {
                CFrameout pcFrameInfo = new CFrameout();
                CDisplayFrameInfo pcDisplayInfo = new CDisplayFrameInfo();
                CPixelConvertParam pcConvertParam = new CPixelConvertParam();

                // 阻塞拉取图像：超时1000ms，无图返回超时错误
                nRet = m_MyCamera.GetImageBuffer(ref pcFrameInfo, 1000);
                if (nRet == CErrorDefine.MV_OK)
                {
                    // 多线程锁：防止UI保存图片和当前帧拷贝同时操作内存
                    lock (BufForDriverLock)
                    {
                        // 克隆当前帧裸图像、帧信息，避免SDK释放缓存后内存失效
                        m_pcImgForDriver = pcFrameInfo.Image.Clone() as CImage;
                        m_pcImgSpecInfo = pcFrameInfo.FrameSpec;

                        // ========== 像素格式转换核心逻辑 ==========
                        pcConvertParam.InImage = pcFrameInfo.Image;
                        if (PixelFormat.Format8bppIndexed == m_pcBitmap.PixelFormat)
                        {
                            // 灰度相机：转Mono8灰度字节流
                            pcConvertParam.OutImage.PixelType = MvGvspPixelType.PixelType_Gvsp_Mono8;
                        }
                        else
                        {
                            // Bayer彩色：SDK自动做拜耳插值，转为BGR8，适配Windows RGB Bitmap
                            pcConvertParam.OutImage.PixelType = MvGvspPixelType.PixelType_Gvsp_BGR8_Packed;
                        }
                        // 调用SDK硬件/软件像素转换，把原始Bayer/Mono转为标准RGB/Mono内存数组
                        m_MyCamera.ConvertPixelType(ref pcConvertParam);

                        // 把转换后的原生字节内存拷贝到GDI Bitmap内存
                        BitmapData m_pcBitmapData = m_pcBitmap.LockBits(
                            new Rectangle(0, 0, pcConvertParam.InImage.Width, pcConvertParam.InImage.Height),
                            ImageLockMode.ReadWrite, m_pcBitmap.PixelFormat);
                        Marshal.Copy(pcConvertParam.OutImage.ImageData, 0, m_pcBitmapData.Scan0, (Int32)pcConvertParam.OutImage.ImageData.Length);
                        m_pcBitmap.UnlockBits(m_pcBitmapData);
                    }

                    // SDK自带渲染：直接把图像绘制到pictureBox窗口句柄，快速预览
                    pcDisplayInfo.WindowHandle = pictureBox1.Handle;
                    pcDisplayInfo.Image = pcFrameInfo.Image;
                    m_MyCamera.DisplayOneFrame(ref pcDisplayInfo);

                    // 必须释放SDK内部缓存！不Free会内存持续暴涨、缓存溢出报错MV_E_BUFOVER
                    m_MyCamera.FreeImageBuffer(ref pcFrameInfo);
                }
                else
                {
                    // 触发模式无图正常休眠，降低CPU占用
                    if (bnTriggerMode.Checked)
                    {
                        Thread.Sleep(5);
                    }
                }
            }
        }

        /// <summary>辅助：判断像素是否为各类Mono灰度</summary>
        private Boolean IsMono(MvGvspPixelType enPixelType)
        {
            switch (enPixelType)
            {
                case MvGvspPixelType.PixelType_Gvsp_Mono1p:
                case MvGvspPixelType.PixelType_Gvsp_Mono2p:
                case MvGvspPixelType.PixelType_Gvsp_Mono4p:
                case MvGvspPixelType.PixelType_Gvsp_Mono8:
                case MvGvspPixelType.PixelType_Gvsp_Mono8_Signed:
                case MvGvspPixelType.PixelType_Gvsp_Mono10:
                case MvGvspPixelType.PixelType_Gvsp_Mono10_Packed:
                case MvGvspPixelType.PixelType_Gvsp_Mono12:
                case MvGvspPixelType.PixelType_Gvsp_Mono12_Packed:
                case MvGvspPixelType.PixelType_Gvsp_Mono14:
                case MvGvspPixelType.PixelType_Gvsp_Mono16:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>开始采集后按钮状态变更</summary>
        private void SetCtrlWhenStartGrab()
        {
            bnStartGrab.Enabled = false;
            bnStopGrab.Enabled = true;

            if (bnTriggerMode.Checked && cbSoftTrigger.Checked)
            {
                bnTriggerExec.Enabled = true;
            }

            // 图片保存按钮解锁
            bnSaveBmp.Enabled = true;
            bnSaveJpg.Enabled = true;
            bnSaveTiff.Enabled = true;
            bnSavePng.Enabled = true;
        }
        #endregion

        #region 停止采集、关闭相机、资源释放
        /// <summary>停止取流按钮</summary>
        private void bnStopGrab_Click(object sender, EventArgs e)
        {
            // 标记停止，等待子线程执行完毕再往下走（Join阻塞等待线程退出，防止内存野指针）
            m_bGrabbing = false;
            m_hReceiveThread.Join();

            // 通知相机硬件停止发送图像流
            int nRet = m_MyCamera.StopGrabbing();
            if (nRet != CErrorDefine.MV_OK)
            {
                ShowErrorMsg("Stop Grabbing Fail!", nRet);
            }

            SetCtrlWhenStopGrab();
        }

        /// <summary>停止采集后UI重置</summary>
        private void SetCtrlWhenStopGrab()
        {
            bnStartGrab.Enabled = true;
            bnStopGrab.Enabled = false;
            bnTriggerExec.Enabled = false;
            bnSaveBmp.Enabled = false;
            bnSaveJpg.Enabled = false;
            bnSaveTiff.Enabled = false;
            bnSavePng.Enabled = false;
        }

        /// <summary>关闭相机按钮：彻底断开硬件、销毁句柄</summary>
        private void bnClose_Click(object sender, EventArgs e)
        {
            // 如果正在采集，先停流、等待线程退出
            if (m_bGrabbing == true)
            {
                m_bGrabbing = false;
                m_hReceiveThread.Join();
            }

            // 关闭硬件TCP/USB连接
            m_MyCamera.CloseDevice();
            // 销毁底层SDK句柄，释放相机占用，其他软件可重新连接
            m_MyCamera.DestroyHandle();

            SetCtrlWhenClose();
        }

        /// <summary>相机关闭后UI全部回归初始不可用状态</summary>
        private void SetCtrlWhenClose()
        {
            bnOpen.Enabled = true;

            bnClose.Enabled = false;
            bnStartGrab.Enabled = false;
            bnStopGrab.Enabled = false;
            bnContinuesMode.Enabled = false;
            bnTriggerMode.Enabled = false;
            cbSoftTrigger.Enabled = false;
            bnTriggerExec.Enabled = false;

            bnSaveBmp.Enabled = false;
            bnSaveJpg.Enabled = false;
            bnSaveTiff.Enabled = false;
            bnSavePng.Enabled = false;
            tbExposure.Enabled = false;
            tbGain.Enabled = false;
            tbFrameRate.Enabled = false;
            bnGetParam.Enabled = false;
            bnSetParam.Enabled = false;
            tbPixelFormat.Enabled = false;
        }

        /// <summary>窗口关闭兜底：强制关闭相机，杜绝硬件占用、内存泄漏</summary>
        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            bnClose_Click(sender, e);
        }
        #endregion

        #region 参数读写：曝光、增益、帧率读写GenICam节点
        /// <summary>读取相机当前全部可配置参数，回显到输入框</summary>
        private void bnGetParam_Click(object sender, EventArgs e)
        {
            CFloatValue pcFloatValue = new CFloatValue();
            CEnumValue pcEnumValue = new CEnumValue();
            CEnumEntry pcEntryValue = new CEnumEntry();
            int nRet = CErrorDefine.MV_OK;

            // 读取触发模式
            GetTriggerMode();

            // 读取曝光时间（单位：微秒 us）GenICam节点 ExpsoureTime
            nRet = m_MyCamera.GetFloatValue("ExposureTime", ref pcFloatValue);
            if (CErrorDefine.MV_OK == nRet)
            {
                tbExposure.Text = pcFloatValue.CurValue.ToString("F1");
            }

            // 读取模拟增益 Gain
            nRet = m_MyCamera.GetFloatValue("Gain", ref pcFloatValue);
            if (CErrorDefine.MV_OK == nRet)
            {
                tbGain.Text = pcFloatValue.CurValue.ToString("F1");
            }

            // 读取实际输出帧率 ResultingFrameRate
            nRet = m_MyCamera.GetFloatValue("ResultingFrameRate", ref pcFloatValue);
            if (CErrorDefine.MV_OK == nRet)
            {
                tbFrameRate.Text = pcFloatValue.ToString();
            }

            // 读取像素格式字符串名称（BayerBG8/Mono8）
            nRet = m_MyCamera.GetEnumValue("PixelFormat", ref pcEnumValue);
            if (CErrorDefine.MV_OK == nRet)
            {
                pcEntryValue.Value = pcEnumValue.CurValue;
                nRet = m_MyCamera.GetEnumEntrySymbolic("PixelFormat", ref pcEntryValue);
                if (CErrorDefine.MV_OK == nRet)
                {
                    tbPixelFormat.Text = pcEntryValue.Symbolic;
                }
            }
        }

        /// <summary>把UI输入的曝光、增益、帧率下发写入相机硬件</summary>
        private void bnSetParam_Click(object sender, EventArgs e)
        {
            // 校验输入为合法数字
            try
            {
                float.Parse(tbExposure.Text);
                float.Parse(tbGain.Text);
                float.Parse(tbFrameRate.Text);
            }
            catch
            {
                ShowErrorMsg("Please enter correct type!", 0);
                return;
            }

            // 关闭自动曝光、自动增益，使用手动固定值
            m_MyCamera.SetEnumValue("ExposureAuto", 0);
            int nRet = m_MyCamera.SetFloatValue("ExposureTime", float.Parse(tbExposure.Text));
            if (nRet != CErrorDefine.MV_OK)
            {
                ShowErrorMsg("Set Exposure Time Fail!", nRet);
            }

            m_MyCamera.SetEnumValue("GainAuto", 0);
            nRet = m_MyCamera.SetFloatValue("Gain", float.Parse(tbGain.Text));
            if (nRet != CErrorDefine.MV_OK)
            {
                ShowErrorMsg("Set Gain Fail!", nRet);
            }

            // 设置目标采集帧率 AcquisitionFrameRate
            nRet = m_MyCamera.SetFloatValue("AcquisitionFrameRate", float.Parse(tbFrameRate.Text));
            if (nRet != CErrorDefine.MV_OK)
            {
                ShowErrorMsg("Set Frame Rate Fail!", nRet);
            }
        }

        /// <summary>单独读取当前触发模式状态，同步UI单选框</summary>
        private void GetTriggerMode()
        {
            CEnumValue pcEnumValue = new CEnumValue();
            int nRet = m_MyCamera.GetEnumValue("TriggerMode", ref pcEnumValue);
            if (CErrorDefine.MV_OK == nRet)
            {
                if ((uint)MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_ON == pcEnumValue.CurValue)
                {
                    bnTriggerMode.Checked = true;
                    bnContinuesMode.Checked = false;

                    // 同步读取触发源
                    nRet = m_MyCamera.GetEnumValue("TriggerSource", ref pcEnumValue);
                    if (CErrorDefine.MV_OK == nRet)
                    {
                        if ((uint)MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE == pcEnumValue.CurValue)
                        {
                            cbSoftTrigger.Checked = true;
                            if (m_bGrabbing)
                            {
                                bnTriggerExec.Enabled = true;
                            }
                            cbSoftTrigger.Enabled = true;
                        }
                    }
                }
                else
                {
                    bnContinuesMode.Checked = true;
                    bnTriggerMode.Checked = false;
                }
            }
        }
        #endregion

        #region 图像保存：BMP/JPG/PNG/TIFF四种格式，调用SDK底层保存接口
        private void bnSaveBmp_Click(object sender, EventArgs e)
        {
            SaveImage(MV_SAVE_IAMGE_TYPE.MV_IMAGE_BMP);
        }
        private void bnSaveJpg_Click(object sender, EventArgs e)
        {
            SaveImage(MV_SAVE_IAMGE_TYPE.MV_IMAGE_JPEG, quality: 80);
        }
        private void bnSavePng_Click(object sender, EventArgs e)
        {
            SaveImage(MV_SAVE_IAMGE_TYPE.MV_IMAGE_PNG, quality: 9);
        }
        private void bnSaveTiff_Click(object sender, EventArgs e)
        {
            SaveImage(MV_SAVE_IAMGE_TYPE.MV_IMAGE_TIF);
        }

        /// <summary>统一封装保存逻辑，复用代码</summary>
        private void SaveImage(MV_SAVE_IAMGE_TYPE imgType, int quality = 0)
        {
            if (false == m_bGrabbing)
            {
                ShowErrorMsg("Not Start Grabbing", 0);
                return;
            }

            CSaveImgToFileParam stSaveFileParam = new CSaveImgToFileParam();
            lock (BufForDriverLock)
            {
                if (m_pcImgForDriver.FrameLen == 0)
                {
                    ShowErrorMsg("Save Fail!", 0);
                    return;
                }
                stSaveFileParam.ImageType = imgType;
                stSaveFileParam.Image = m_pcImgForDriver;
                stSaveFileParam.Quality = (uint)quality;
                stSaveFileParam.MethodValue = 2;
                // 自动拼接文件名：宽_高_帧号.后缀
                stSaveFileParam.ImagePath = $"Image_w{stSaveFileParam.Image.Width}_h{stSaveFileParam.Image.Height}_fn{m_pcImgSpecInfo.FrameNum}.{imgType.ToString().Split('_').Last().ToLower()}";
                int nRet = m_MyCamera.SaveImageToFile(ref stSaveFileParam);
                if (CErrorDefine.MV_OK != nRet)
                {
                    ShowErrorMsg("Save Fail!", nRet);
                    return;
                }
            }
            ShowErrorMsg("Save Succeed!", 0);
        }
        #endregion

        private void Form1_Load(object sender, EventArgs e)
        {

        }
    }
}