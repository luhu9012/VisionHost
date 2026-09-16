using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HalconDotNet;
using VisualCalibTool.Algorithm;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Imaging
{
    /// <summary>
    /// ★★ HALCON 相机标定链的封装：<c>create_calib_data</c> → <c>set_calib_data_cam_param</c>
    /// → <c>set_calib_data_calib_object</c> → <c>find_calib_object</c> → <c>calibrate_cameras</c>
    /// → <c>get_calib_data</c>。
    ///
    /// 这一层存在的唯一理由：<b>把 HALCON 的坑全部收口在一处</b>。下面每一条都是本轮
    /// 真机上跑出来、而不是查文档"觉得应该这样"的：
    ///
    /// ① <b>相机参数是混合元组</b>（第 0 项是类型字符串），必须用
    ///    <c>new HTuple(object[])</c> 构造。用 <c>TupleConcat</c> 把数字元组与字符串拼起来，
    ///    整条会<b>退化成字符串元组</b> → #1203 Wrong type。
    /// ② <c>set_calib_data_cam_param</c> 的 cameraType <b>不能传空元组</b>（#1403）。
    /// ③ <b><c>calib_obj_pose</c> 也是待优化量</b>，标定之前取不到（#8451 Model not optimized yet）。
    ///    逐张重投影必须放在 <c>calibrate_cameras</c> <b>之后</b>，用解出来的参数去做。
    /// ④ <c>params</c> 是 <b>9 项</b>（含 camera_type），<c>params_deviations</c> 只有 <b>6 项</b>
    ///    （少了开头的 camera_type 和结尾的 image_width / image_height）。按"长度差"硬贴会
    ///    <b>整体错位</b>，把别人的标准差当成 kappa 的，得出"kappa 精度 1e-12"这种荒谬的自信。
    /// ⑤ ★ <b>κ 的量纲</b>：HALCON「除法模型」的 kappa 乘的是<b>像面公制半径（米）</b>，
    ///    量纲 1/m²，与常见的归一化 k1 差一个 <c>f²</c>。把 k1 = −0.15 直接当 κ 用等于
    ///    "没有畸变"，会让"注入-解回"变成自证。换算见 <see cref="IntrinsicsGeometry.KappaFromNormalized"/>。
    /// ⑥ <b>板法向不要自己从 pose 反解</b>：HALCON 的 pose 有 7 个元素（含编码），
    ///    绕轴顺序 <c>'gba'</c> 是一套约定，自己写一遍就是埋一个只有出错才暴露的坑。
    ///    统一走 <c>pose_to_hom_mat3d</c> 取旋转矩阵的<b>第三列</b>。
    /// </summary>
    public sealed class HalconBoardDetector : IDisposable
    {
        private const string AreaScanDivision = "area_scan_division";

        private readonly BoardModel _board;
        private readonly CameraIntrinsicsGuess _guess;
        private readonly List<BoardViewFact> _views = new List<BoardViewFact>();
        private readonly List<string> _viewErrors = new List<string>();
        private readonly List<string> _geometryTrace = new List<string>();

        private HTuple _calibData;
        private string _lastRawPose = "-";
        private bool _disposed;
        private int _poseCount;

        /// <summary>初始内参猜测的文本（写进报告，便于复盘"一开始填的是什么"）。</summary>
        public string GuessText;

        public HalconBoardDetector(BoardModel board, CameraIntrinsicsGuess guess)
        {
            if (board == null)
            {
                throw new ArgumentNullException("board");
            }

            if (guess == null)
            {
                throw new ArgumentNullException("guess");
            }

            if (!board.IsAvailable)
            {
                throw new InvalidOperationException("标定板模型不可用：" + board.SearchHint);
            }

            _board = board;
            _guess = guess;
            GuessText = guess.Describe();

            // ★ 铁律 ① + ②：类型字符串必须在元组里，且 cameraType 参数不能为空元组
            HTuple camPar = new HTuple(new object[]
            {
                AreaScanDivision,
                guess.FocalM,
                guess.Kappa,
                guess.PixelPitchM,
                guess.PixelPitchM,
                guess.Cx,
                guess.Cy,
                (long)guess.Width,
                (long)guess.Height
            });

            HOperatorSet.CreateCalibData("calibration_object", 1, 1, out _calibData);
            HOperatorSet.SetCalibDataCamParam(_calibData, 0, new HTuple(AreaScanDivision), camPar);

            object descr = board.CalibObjectDescriptor();
            var d = descr as object[];
            if (d != null)
            {
                // 棋盘格：3D 点元组（单位米）
                HOperatorSet.SetCalibDataCalibObject(_calibData, 0, new HTuple(d));
            }
            else
            {
                HOperatorSet.SetCalibDataCalibObject(_calibData, 0, new HTuple((string)descr));
            }
        }

        /// <summary>已成功加入的姿态事实（按加入顺序）。</summary>
        public IList<BoardViewFact> Views
        {
            get { return _views; }
        }

        /// <summary>已尝试但失败的图（人话原因），给"重拍"提示用。</summary>
        public IList<string> ViewErrors
        {
            get { return _viewErrors; }
        }

        /// <summary>
        /// 逐张的几何读回轨迹（板位姿原始值 → 法向 → 中心深度）。
        /// ★ 留着它的理由：一旦"覆盖度说全都不斜"，光看结论没法判断是
        ///   "用户真没斜着拍" 还是 "我们把位姿读错了"。有了这条轨迹一行就能分。
        /// </summary>
        public IList<string> ViewGeometryTrace
        {
            get { return _geometryTrace; }
        }

        public int AcceptedPosCount
        {
            get { return _poseCount; }
        }

        /// <summary>
        /// 解析"这张图相对画面中心在哪个方向"，用于给操作员的位置提示。
        /// 板没进画面 / 太偏时能让操作员立刻知道该往哪挪。
        /// </summary>
        public static string OffCenterHint(double row, double col, int width, int height)
        {
            double dx = (col - (width - 1) / 2.0) / (width / 2.0);
            double dy = (row - (height - 1) / 2.0) / (height / 2.0);
            if (Math.Abs(dx) < 0.15 && Math.Abs(dy) < 0.15)
            {
                return "板在画面中央";
            }

            var sb = new StringBuilder("板偏在画面");
            sb.Append(dy < -0.15 ? "上" : (dy > 0.15 ? "下" : string.Empty));
            sb.Append(dx < -0.15 ? "左" : (dx > 0.15 ? "右" : string.Empty));
            sb.Append(string.Format(CultureInfo.InvariantCulture, "（偏离中心 {0:P0} / {1:P0}）", Math.Abs(dx), Math.Abs(dy)));
            return sb.ToString();
        }

        /// <summary>
        /// 裸帧版本（推荐入口）：<b>只吃 byte[] 与尺寸</b>，HALCON 类型不出这一层。
        /// 这样上层的编排器 / 视图模型源码里不会出现任何 <c>HalconDotNet</c> 名字，
        /// 也就不会有人顺手在 UI 线程里 new 一个 HObject 出来。
        /// </summary>
        public bool TryAddViewRaw(string label, byte[] rawGray, int width, int height,
            out string error, out BoardViewFact fact)
        {
            fact = new BoardViewFact();
            error = null;

            if (rawGray == null || rawGray.Length == 0)
            {
                error = "没有帧数据（取图失败或超时）。";
                _viewErrors.Add(label + "：" + error);
                return false;
            }

            HImage image = null;
            try
            {
                image = HalconFrameSource.FromRawGray(rawGray, width, height);
            }
            catch (Exception ex)
            {
                error = "帧数据不合法（" + width + "×" + height + " 解析失败）：" + OneLine(ex.Message);
                _viewErrors.Add(label + "：" + error);
                return false;
            }

            try
            {
                return TryAddView(label, image, out error, out fact);
            }
            finally
            {
                try
                {
                    if (image != null && image.IsInitialized())
                    {
                        image.Dispose();
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        // ------------------------------------------------------------------ 单张：找板

        /// <summary>
        /// 在当前这张图上找板并登记为一个姿态。返回是否成功。
        /// ★ 失败不抛异常：一张图找不到板是<b>正常可恢复</b>的事（反光、手抖、板出界），
        ///   操作员应该能接着重拍，而不是整条链从头再来。
        /// </summary>
        public bool TryAddView(string label, HObject image, out string error, out BoardViewFact fact)
        {
            fact = new BoardViewFact();
            error = null;

            if (image == null)
            {
                error = "没有图像（取图为空）。";
                _viewErrors.Add(label + "：" + error);
                return false;
            }

            int poseIdx = _poseCount;
            try
            {
                HOperatorSet.FindCalibObject(image, _calibData, 0, 0, poseIdx, new HTuple(), new HTuple());
            }
            catch (Exception ex)
            {
                error = DescribeFindFailure(ex);
                _viewErrors.Add(label + "：" + error);
                return false;
            }

            HTuple rows, cols, ids, pose;
            HOperatorSet.GetCalibDataObservPoints(_calibData, 0, 0, poseIdx, out rows, out cols, out ids, out pose);
            int markCount = rows.TupleLength();
            if (markCount <= 0)
            {
                error = "找到板但一个 mark 都没取到。";
                _viewErrors.Add(label + "：" + error);
                return false;
            }

            fact.Label = label;
            fact.MarkCount = markCount;

            // ★ 这里**不读**板位姿：calib_obj_pose 本身是待优化量，标定之前取不到
            //   （HALCON #8451 Model not optimized yet）。位姿统一在 Solve() 里、标定之后回填。
            //   实测过一次踩坑：在找板时就读，结果覆盖度统计全是 0 —— 而残差、κ 全都正常，
            //   看上去像是"标定成功但判据失灵"，其实是判据的输入根本没拿到。
            fact.CenterZ = double.NaN;

            // 顺带给操作员两句话：
            //   ① "板在哪"（板偏在画面哪一侧，界面用它提示下次往哪挪）；
            //   ② "板多大"（★ 关键：HALCON 会把小于期望尺寸的 mark 当噪声整片剔掉，
            //      所以视在尺寸是"找不着板"的头号原因 —— 实测 231 mm 时板宽 633 px
            //      （占画面 49%）检出 818 个 mark；到 240 mm 只剩 609 px（47.6%）就
            //      直接掉到 0 个。断崖式，用户完全无从理解，必须由界面翻译成一句话。）
            double meanRow = 0.0, meanCol = 0.0;
            double minRow = double.MaxValue, maxRow = double.MinValue;
            double minCol = double.MaxValue, maxCol = double.MinValue;
            for (int i = 0; i < markCount; i++)
            {
                double r = rows[i].D;
                double c = cols[i].D;
                meanRow += r;
                meanCol += c;
                if (r < minRow) { minRow = r; }
                if (r > maxRow) { maxRow = r; }
                if (c < minCol) { minCol = c; }
                if (c > maxCol) { maxCol = c; }
            }

            fact.MeanRow = meanRow / markCount;
            fact.MeanCol = meanCol / markCount;
            fact.ApparentWidthPx = maxCol - minCol;
            fact.ApparentHeightPx = maxRow - minRow;

            // 检出点留一份（只给显示用：界面把它画回画面，好让操作员当场分辨
            // "板太小被当噪声剔了" 与 "软件根本没在找" —— 只看总数分辨不了）。
            var markRows = new double[markCount];
            var markCols = new double[markCount];
            for (int i = 0; i < markCount; i++)
            {
                markRows[i] = rows[i].D;
                markCols[i] = cols[i].D;
            }

            fact.MarkRows = markRows;
            fact.MarkCols = markCols;

            // ★ 视在大小改用"相邻 mark 的像面间距 ×（列数−1）"来估。
            //   外接框在倾斜 / 部分检出时严重偏小（实测倾斜 25° 只认出 387/837，
            //   据此算出来"板只占画面 25%"是假结论），而相邻间距是局部量，稳得多。
            double spacing = MedianNeighbourSpacing(ids, rows, cols, _board.MarkCols);
            if (spacing > 0.0)
            {
                fact.ApparentSpacingPx = spacing;
                int gridCols = _board.MarkCols > 1 ? _board.MarkCols - 1 : 1;
                int gridRows = _board.MarkRows > 1 ? _board.MarkRows - 1 : 1;
                fact.ApparentWidthPx = spacing * gridCols;
                fact.ApparentHeightPx = spacing * gridRows;
            }

            _views.Add(fact);
            _poseCount++;
            return true;
        }

        /// <summary>
        /// 相邻 mark 像面间距的中位数（像素）。
        /// 只在"板上确实相邻"的一对之间量：同一行的右邻（序号 +1，且不跨行）与正下方（序号 +列数）。
        /// 这样量出来的是真实间距，而不是两个随机点之间的距离。
        /// </summary>
        private static double MedianNeighbourSpacing(HTuple ids, HTuple rows, HTuple cols, int gridCols)
        {
            if (gridCols < 2 || ids.TupleLength() < 4)
            {
                return 0.0;
            }

            var pos = new Dictionary<int, int>();
            for (int i = 0; i < ids.TupleLength(); i++)
            {
                pos[ids[i].I] = i;
            }

            var d = new List<double>();
            for (int i = 0; i < ids.TupleLength(); i++)
            {
                int id = ids[i].I;

                // 同一行的右邻：id+1 不跨行
                if (id % gridCols != gridCols - 1)
                {
                    AddPairDistance(d, pos, rows, cols, i, id + 1);
                }

                // 正下方一个
                AddPairDistance(d, pos, rows, cols, i, id + gridCols);
            }

            if (d.Count == 0)
            {
                return 0.0;
            }

            d.Sort();
            return d[d.Count / 2];
        }

        private static void AddPairDistance(List<double> sink, Dictionary<int, int> pos,
            HTuple rows, HTuple cols, int i, int wantId)
        {
            int j;
            if (!pos.TryGetValue(wantId, out j))
            {
                return;
            }

            double dr = rows[i].D - rows[j].D;
            double dc = cols[i].D - cols[j].D;
            double dist = Math.Sqrt(dr * dr + dc * dc);
            if (dist > 0.5)
            {
                sink.Add(dist);
            }
        }

        private static string DescribeFindFailure(Exception ex)
        {
            string msg = ex is HOperatorException
                ? ((HOperatorException)ex).GetErrorMessage()
                : ex.Message;

            if (msg != null && msg.IndexOf("8397", StringComparison.Ordinal) >= 0)
            {
                return "板上还没找到 mark（HALCON #8397 区域分割失败）：多半是曝光过亮/过暗、"
                     + "反光打花了 mark、或者板只占画面很小一角。调光后重拍。";
            }

            if (msg != null && msg.IndexOf("8399", StringComparison.Ordinal) >= 0)
            {
                return "找不到定位图案（HALCON #8399）：板被画面边缘切掉了，或者倾斜太大导致角上的"
                     + "定位图案出视野。把板整体挪回画面内、倾斜减小到 30° 以内。";
            }

            if (msg != null && msg.IndexOf("8396", StringComparison.Ordinal) >= 0)
            {
                return "板上定位图案与 mark 的几何关系不对（HALCON #8396）："
                     + "多半是板文件与实物不是同一块（40 mm 的板配了 80 mm 的文件）。";
            }

            return "找板失败：" + OneLine(msg);
        }

        /// <summary>
        /// 从板位姿取「法向」与「板中心深度」。
        /// ★ 走 <c>pose_to_hom_mat3d</c> 取旋转矩阵第三列，不自己反解 pose 的三个编码。
        /// </summary>
        private bool TryReadPoseGeometry(int poseIdx, out double nx, out double ny, out double nz,
            out double cx, out double cy, out double cz)
        {
            nx = 0.0;
            ny = 0.0;
            nz = -1.0;
            cx = 0.0;
            cy = 0.0;
            cz = double.NaN;

            HTuple pose, hom;
            try
            {
                HOperatorSet.GetCalibData(_calibData, "calib_obj_pose", new HTuple(0, poseIdx), "pose", out pose);
                _lastRawPose = DumpTuple(pose);
                HOperatorSet.PoseToHomMat3d(pose, out hom);
            }
            catch (Exception)
            {
                return false;      // 标定前取不到是正常的（#8451）—— 所以本方法只能在 Solve() 之后调
            }

            // HomMat3D 是 16 个值的行主序 4×4：R 的第三列 = 下标 2 / 6 / 10
            double n1 = hom[2].D;
            double n2 = hom[6].D;
            double n3 = hom[10].D;
            double len = Math.Sqrt(n1 * n1 + n2 * n2 + n3 * n3);
            if (len > 1e-12)
            {
                nx = n1 / len;
                ny = n2 / len;
                nz = n3 / len;
            }

            // 板中心 = 板坐标系里所有 mark 点的均值，再用同一个位姿变换到相机系
            try
            {
                HTuple mx, my, mz;
                HOperatorSet.GetCalibData(_calibData, "calib_obj", 0, "x", out mx);
                HOperatorSet.GetCalibData(_calibData, "calib_obj", 0, "y", out my);
                HOperatorSet.GetCalibData(_calibData, "calib_obj", 0, "z", out mz);
                int n = mx.TupleLength();
                if (n > 0)
                {
                    HTuple ax, ay, az;
                    HOperatorSet.AffineTransPoint3d(hom,
                        new HTuple(Mean(mx, n)), new HTuple(Mean(my, n)), new HTuple(Mean(mz, n)),
                        out ax, out ay, out az);
                    cx = ax[0].D;
                    cy = ay[0].D;
                    cz = az[0].D;
                }
            }
            catch (Exception)
            {
                // 中心取不到就退回用位姿的平移分量当深度近似（覆盖度跨度仍大致可用）
                cz = pose.TupleLength() >= 3 ? pose[2].D : double.NaN;
            }

            return true;
        }

        private static double Mean(HTuple t, int n)
        {
            double s = 0.0;
            for (int i = 0; i < n; i++)
            {
                s += t[i].D;
            }

            return n > 0 ? s / n : 0.0;
        }

        // ------------------------------------------------------------------ 标定

        /// <summary>
        /// 跑 <c>calibrate_cameras</c>，读回内参，并逐张算重投影。
        /// ★ 逐张重投影在这里做（标定之后），因为板位姿本身也是标定解出来的量 ——
        ///   标定之前取它会得到 #8451 Model not optimized yet。
        /// </summary>
        public IntrinsicsSolveOutcome Solve()
        {
            var outcome = new IntrinsicsSolveOutcome();
            outcome.ViewCount = _views.Count;

            if (_poseCount < 3)
            {
                outcome.Success = false;
                outcome.Error = CalibError.Create(CalibFailureKind.SolveFailed,
                    string.Format(CultureInfo.InvariantCulture,
                        "只成功找到 {0} 张板（至少 3 张）。内参有焦距、畸变、主点等 5 个以上未知量，"
                        + "图太少时任何「成功」都是数值巧合。", _poseCount));
                return outcome;
            }

            HTuple error;
            try
            {
                HOperatorSet.CalibrateCameras(_calibData, out error);
            }
            catch (Exception ex)
            {
                string msg = ex is HOperatorException
                    ? ((HOperatorException)ex).GetErrorMessage()
                    : ex.Message;
                outcome.Success = false;
                outcome.Error = CalibError.Create(CalibFailureKind.SolveFailed,
                    "相机标定失败：" + OneLine(msg)
                    + "（常见原因：所有图都是同一个姿态 / 板位姿差别太小，参数之间无法分开，"
                    + "矩阵接近奇异。）");
                return outcome;
            }

            outcome.RmsePx = error.TupleLength() > 0 ? error[0].D : double.NaN;

            HTuple labels, values;
            HOperatorSet.GetCalibData(_calibData, "camera", 0, "params_labels", out labels);
            HOperatorSet.GetCalibData(_calibData, "camera", 0, "params", out values);

            outcome.RawParams = LabeledDump(labels, values, 0);
            double focusM = ReadValue(labels, values, "focus");
            double kappa = ReadValue(labels, values, "kappa");
            double sx = ReadValue(labels, values, "sx");
            double sy = ReadValue(labels, values, "sy");
            double cx = ReadValue(labels, values, "cx");
            double cy = ReadValue(labels, values, "cy");
            double imgW = ReadValue(labels, values, "image_width");
            double imgH = ReadValue(labels, values, "image_height");

            var ir = new IntrinsicsResult();
            ir.FocalLengthPx = new double[]
            {
                sx > 1e-12 ? focusM / sx : double.NaN,
                sy > 1e-12 ? focusM / sy : double.NaN
            };
            ir.PrincipalPointPx = new double[] { cx, cy };
            ir.Distortion = new double[] { kappa };
            ir.ImageSize = new int[]
            {
                imgW > 0 ? (int)Math.Round(imgW) : _guess.Width,
                imgH > 0 ? (int)Math.Round(imgH) : _guess.Height
            };
            ir.UsedPoseCount = _poseCount;
            ir.Success = true;

            outcome.FocusM = focusM;
            outcome.KappaMetric = kappa;
            outcome.KappaNormalized = IntrinsicsGeometry.NormalizedFromKappa(kappa, focusM);
            outcome.PixelPitchM = sx > 1e-12 ? sx : _guess.PixelPitchM;
            outcome.Width = ir.ImageSize[0];
            outcome.Height = ir.ImageSize[1];

            // 参数标准差：★ 6 项，少了开头的 camera_type 与结尾的 image_width / image_height
            try
            {
                HTuple dev;
                HOperatorSet.GetCalibData(_calibData, "camera", 0, "params_deviations", out dev);
                outcome.RawDeviations = LabeledDump(labels, dev, 1);
                double fdev = ReadValue(labels, dev, "focus");
                double kdev = ReadValue(labels, dev, "kappa");
                outcome.FocusRelDev = Math.Abs(focusM) > 1e-12 ? Math.Abs(fdev / focusM) : double.NaN;
                outcome.KappaRelDev = Math.Abs(kappa) > 1e-12 ? Math.Abs(kdev / kappa) : double.NaN;
            }
            catch (Exception)
            {
                // 偏差不是所有模型都给；缺了不影响主结论
            }

            // 逐张重投影 + 回填板位姿几何（★ 都必须在 calibrate_cameras 之后）
            var facts = new List<BoardViewFact>();
            var perView = new List<double>();
            for (int i = 0; i < _poseCount && i < _views.Count; i++)
            {
                double rms = PoseReprojectionRms(i, values);

                BoardViewFact f = _views[i];
                f.ResidualPx = rms;

                double nx, ny, nz, bx, by, bz;
                if (TryReadPoseGeometry(i, out nx, out ny, out nz, out bx, out by, out bz))
                {
                    f.NormalX = nx;
                    f.NormalY = ny;
                    f.NormalZ = nz;
                    f.CenterX = bx;
                    f.CenterY = by;
                    f.CenterZ = bz;
                    _geometryTrace.Add(string.Format(CultureInfo.InvariantCulture,
                        "#{0} {1}：位姿={2} | 法向=({3:F4}, {4:F4}, {5:F4}) 倾角={6:F2}° | 中心=({7:F1}, {8:F1}, {9:F1}) mm 深度={10:F1} mm",
                        i, f.Label, _lastRawPose, nx, ny, nz, f.TiltDeg,
                        bx * 1000.0, by * 1000.0, bz * 1000.0, bz * 1000.0));
                }
                else
                {
                    _viewErrors.Add(f.Label + "：标定后仍取不到板位姿（覆盖度会少统计这一张）。");
                    _geometryTrace.Add("#" + i + " " + f.Label + "：标定后仍取不到板位姿");
                }

                perView.Add(rms);
                facts.Add(f);
            }

            // 没找到板的图也留在列表里（界面要能显示"哪几张失败了"）
            for (int i = _poseCount; i < _views.Count; i++)
            {
                facts.Add(_views[i]);
            }

            ir.ReprojectionErrorsPx = perView.ToArray();
            double sum = 0.0;
            int cnt = 0;
            for (int i = 0; i < perView.Count; i++)
            {
                if (!double.IsNaN(perView[i]))
                {
                    sum += perView[i];
                    cnt++;
                }
            }

            ir.MeanReprojectionErrorPx = cnt > 0 ? sum / cnt : double.NaN;

            outcome.Intrinsics = ir;
            outcome.Views = facts;
            outcome.Success = true;
            return outcome;
        }

        /// <summary>
        /// 单张图的重投影 RMS：把板模型点用解出的位姿摆到相机系，再用解出的内参投到像面，
        /// 和实际检出的亚像素 mark 位置比。<b>按 mark 序号配对，不是按顺序硬配</b> ——
        /// 边缘 mark 会被漏检，按顺序配会把整张图的误差算成"均匀偏大"。
        /// </summary>
        private double PoseReprojectionRms(int poseIdx, HTuple cameraParam)
        {
            try
            {
                HTuple mx, my, mz, pose, hom;
                HOperatorSet.GetCalibData(_calibData, "calib_obj", 0, "x", out mx);
                HOperatorSet.GetCalibData(_calibData, "calib_obj", 0, "y", out my);
                HOperatorSet.GetCalibData(_calibData, "calib_obj", 0, "z", out mz);
                HOperatorSet.GetCalibData(_calibData, "calib_obj_pose", new HTuple(0, poseIdx), "pose", out pose);
                HOperatorSet.PoseToHomMat3d(pose, out hom);

                HTuple cxT, cyT, czT;
                HOperatorSet.AffineTransPoint3d(hom, mx, my, mz, out cxT, out cyT, out czT);

                HTuple predRow, predCol;
                HOperatorSet.Project3dPoint(cxT, cyT, czT, cameraParam, out predRow, out predCol);

                HTuple obsRow, obsCol, obsIdx, obsPose;
                HOperatorSet.GetCalibDataObservPoints(_calibData, 0, 0, poseIdx,
                    out obsRow, out obsCol, out obsIdx, out obsPose);

                int n = obsIdx.TupleLength();
                double sum = 0.0;
                int used = 0;
                for (int k = 0; k < n; k++)
                {
                    int id = obsIdx[k].I;
                    if (id < 0 || id >= predRow.TupleLength())
                    {
                        continue;
                    }

                    double dr = obsRow[k].D - predRow[id].D;
                    double dc = obsCol[k].D - predCol[id].D;
                    sum += dr * dr + dc * dc;
                    used++;
                }

                return used > 0 ? Math.Sqrt(sum / used) : double.NaN;
            }
            catch (Exception)
            {
                return double.NaN;
            }
        }

        // ------------------------------------------------------------------ 取值辅助

        /// <summary>
        /// 按 <c>params_labels</c> 的名字取值。
        /// ★ <paramref name="shift"/> 是"值元组相对名字元组要往后串几位"：
        ///   <c>params</c> 与原标签一一对应（shift = 0）；
        ///   <c>params_deviations</c> 少了开头的 camera_type 与结尾的两个尺寸（shift = 1）。
        ///   规则写死在调用处，不做"长度差"推断 —— 长度差是 3，不是 1，推断出来的结论是错的。
        /// </summary>
        private static double ReadValue(HTuple labels, HTuple values, string name)
        {
            if (labels == null || values == null)
            {
                return double.NaN;
            }

            int k = LabelIndex(labels, name);
            if (k < 0)
            {
                return double.NaN;
            }

            int idx = k - (values.TupleLength() == labels.TupleLength() ? 0 : 1);
            if (idx < 0 || idx >= values.TupleLength())
            {
                return double.NaN;
            }

            try
            {
                return values.TupleSelect(new HTuple(idx)).D;
            }
            catch (Exception)
            {
                return double.NaN;
            }
        }

        private static int LabelIndex(HTuple labels, string name)
        {
            int n = labels.TupleLength();
            for (int i = 0; i < n; i++)
            {
                string label;
                try
                {
                    label = labels.TupleSelect(new HTuple(i)).S;
                }
                catch (Exception)
                {
                    continue;
                }

                if (label == null)
                {
                    continue;
                }

                string low = label.ToLowerInvariant();
                if (low == name || low.EndsWith("_" + name, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>把元组按标签打印出来（人眼复盘用）。★ 不能用 HTupleElements.ToString()，它给的是类型名。</summary>
        private static string LabeledDump(HTuple labels, HTuple values, int shift)
        {
            var sb = new StringBuilder();
            int ln = labels.TupleLength();
            int vn = values.TupleLength();
            for (int i = 0; i < vn; i++)
            {
                if (i > 0)
                {
                    sb.Append("  ");
                }

                int li = i + shift;
                sb.Append(li >= 0 && li < ln ? Elem(labels, li) : "?").Append('=')
                  .Append(Elem(values, i));
            }

            return sb.ToString();
        }

        private static string Elem(HTuple t, int i)
        {
            if (t == null || i < 0 || i >= t.TupleLength())
            {
                return "-";
            }

            HTuple one = t.TupleSelect(new HTuple(i));
            switch (one.Type)
            {
                case HTupleType.INTEGER:
                    return one.I.ToString(CultureInfo.InvariantCulture);
                case HTupleType.DOUBLE:
                    return one.D.ToString("G6", CultureInfo.InvariantCulture);
                case HTupleType.STRING:
                    return one.S;
                default:
                    return one.ToString();
            }
        }

        private static string DumpTuple(HTuple t)
        {
            var sb = new StringBuilder("(");
            for (int i = 0; i < t.TupleLength(); i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(Elem(t, i));
            }

            return sb.Append(')').ToString();
        }

        private static string OneLine(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "(无信息)";
            }

            string t = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return t.Length > 200 ? t.Substring(0, 200) + "…" : t;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                if (_calibData != null && _calibData.TupleLength() > 0)
                {
                    HOperatorSet.ClearCalibData(_calibData);
                }
            }
            catch (Exception)
            {
                // 释放失败不抛：走到这里抛出去只会掩盖真正的业务异常
            }
        }
    }
}
