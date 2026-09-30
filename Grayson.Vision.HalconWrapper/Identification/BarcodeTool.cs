//===================================================================================
// 文件名: BarcodeTool.cs
// 说 明: 一维码 / 二维码（Data Matrix、QR）识别工具 —— 真实 HALCON 实现。
//
// ⚠ 2026-09-28：由 mock 改为真实实现。原实现（框架底座）在 image != null 时
//   **恒定返回 Success=true + TextResult="SAMPLE_BARCODE_12345"**（连 TODO 都没有），
//   链路"跑通并给出结果"，但门禁一处都不会红 —— 与 ColorTool 当年那个
//   "恒定 AreaRatio=85.5" 属同一类最危险的静默 mock。
//
// 真实链路（与 HTH 课堂案例 6一维码识别 / 6二维码识别.hdev 同思路，算子全部既有）：
//   【一维码】
//     rgb1_to_gray（若为彩色）→ create_bar_code_model([])
//       → set_bar_code_param：majority_voting（多线投票，结果更准）
//                             stop_after_result_num（找够即停）
//                             element_size_min/max（条宽像素范围，小码必须放宽）
//       → find_bar_code(image, region, model, CodeType, DataStrings)
//       → get_bar_code_result(... 'decoded_types') 得实际码制（供展示/回填）
//   【二维码 / Data Matrix】
//     rgb1_to_gray → create_data_code_2d_model('QR Code'|'Data Matrix ECC 200'|'Aztec Code'|'PDF417')
//       → set_data_code_2d_param('polarity', 'dark_on_light'|'light_on_dark'|'any')
//       → find_data_code_2d(image, region, model, [], [], ResultHandles, DataStrings)
//
// 为什么 timeoutMs 参数保留但当前不参与 HALCON 调用：
//   HALCON 的 find_bar_code / find_data_code_2d 本身不接受超时（'timeout' 属部分码制
//   的 set_*_param 键，且各版本支持面不一致）；节点参数里的 TimeoutMs 目前只作
//   语义占位与日志记录，**不假冒成"已生效"**（判据纪律：宁可不实现，不可假装实现）。
//
// 结果区域：返回 HALCON 的 XLD 轮廓对象（SymbolRegions / SymbolXLDs），
//   下游预览按颜色叠加；调用方不得释放（本工具返回的是新建对象，由调用方持有）。
//===================================================================================
using System;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Identification
{
    /// <summary>条码/二维码识别结果：TextResult=解码文本（多码用分隔符连接），BarcodeRegion=符号 XLD 轮廓。</summary>
    public class BarcodeReadResult
    {
        public bool Success { get; set; }
        public string TextResult { get; set; } = string.Empty;
        /// <summary>符号区域 XLD 轮廓（可能是多个对象，需 CountObj 遍历显示）</summary>
        public object BarcodeRegion { get; set; }
        public string Message { get; set; } = string.Empty;
        /// <summary>实际识别的码制（一维码由 get_bar_code_result 回读；二维码即模型类型）</summary>
        public string DecodedType { get; set; } = string.Empty;
        /// <summary>识别到的符号个数</summary>
        public int Count { get; set; }
    }

    /// <summary>条码识别码制（与节点 Param 的枚举值保持整数一致，勿随意调序）。</summary>
    public enum BarcodeKind
    {
        /// <summary>0 = 自动（一维码走 'auto'；二维码按 CodeTypeAuto2D 参数选模型）</summary>
        Auto = 0,
        Code128 = 1,
        Code39 = 2,
        EAN13 = 3,
        QRCode = 4,
        DataMatrix = 5,
        /// <summary>6 = UPC（EAN13 模型可覆盖大部分 UPC-A，单独列出便于显式指定）</summary>
        UPC = 6,
        /// <summary>7 = Aztec Code（二维码族）</summary>
        Aztec = 7,
        /// <summary>8 = PDF417（二维码族）</summary>
        PDF417 = 8
    }

    public static class BarcodeTool
    {
        /// <summary>
        /// 识别一维码 / 二维码。
        /// </summary>
        /// <param name="image">输入图像（HObject/HImage，运行时必须是 HALCON 图像对象）</param>
        /// <param name="region">搜索区域（HObject 区域；null=全图）</param>
        /// <param name="codeType">码制（<see cref="BarcodeKind"/> 的整数值）</param>
        /// <param name="maxCount">期望最大码数（一维码 stop_after_result_num；二维码按结果数截断）</param>
        /// <param name="timeoutMs">语义占位（HALCON 算子无此入参，仅记日志，不假冒生效）</param>
        /// <param name="elementSizeMin">条/码元最小像素宽（小码识别关键；0=用 HALCON 默认）</param>
        /// <param name="polarity">极性：'any'/'dark_on_light'/'light_on_dark'</param>
        public static BarcodeReadResult ReadBarcode(object image, object region, int codeType,
            int maxCount, int timeoutMs, double elementSizeMin = 0, string polarity = "any")
        {
            var hImg = image as HObject;
            if (hImg == null || !hImg.IsInitialized())
                return Fail("输入图像为空或未初始化");

            var roi = region as HObject;
            bool hasRoi = roi != null && roi.IsInitialized();

            HObject gray = null;          // 单通道工作图（原图未给 ROI 时可能直接引用输入）
            HObject work = null;          // ROI 裁剪后的工作图
            bool ownGray = false, ownWork = false;
            HTuple barModel = null, dc2dModel = null;
            try
            {
                // ---- 0) 转灰度（彩色图必须先降通道，HALCON 识码算子只吃单通道）----
                HOperatorSet.CountChannels(hImg, out HTuple channels);
                if (channels.Length > 0 && channels[0].I > 1)
                {
                    HOperatorSet.Rgb1ToGray(hImg, out gray);
                    ownGray = true;
                }
                else
                {
                    gray = hImg;
                }

                // ---- 1) ROI 裁剪（reduce_domain 而非 crop_domain：保留原坐标系，结果轮廓可原图叠加）----
                if (hasRoi)
                {
                    HOperatorSet.ReduceDomain(gray, roi, out work);
                    ownWork = true;
                }
                else
                {
                    work = gray;
                }

                var kind = (BarcodeKind)codeType;
                bool is2D = kind == BarcodeKind.QRCode || kind == BarcodeKind.DataMatrix
                            || kind == BarcodeKind.Aztec || kind == BarcodeKind.PDF417;

                var res = is2D
                    ? Read2D(work, kind, maxCount, polarity, ref dc2dModel)
                    : Read1D(work, kind, maxCount, elementSizeMin, ref barModel);

                if (res.Success && timeoutMs > 0)
                    res.Message += $"（TimeoutMs={timeoutMs} 为节点语义占位，HALCON 识码算子无超时入参）";

                return res;
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(BarcodeTool), "条码识别失败", ex);
                return Fail("条码识别异常: " + ex.Message);
            }
            finally
            {
                // 只释放本方法自建的中间对象；返回的 XLD 轮廓归调用方
                if (barModel != null) { try { HOperatorSet.ClearBarCodeModel(barModel); } catch { } }
                if (dc2dModel != null) { try { HOperatorSet.ClearDataCode2dModel(dc2dModel); } catch { } }
                if (ownWork) work?.Dispose();
                if (ownGray) gray?.Dispose();
            }
        }

        // ==================================================================
        // 一维码
        // ==================================================================
        private static BarcodeReadResult Read1D(HObject work, BarcodeKind kind, int maxCount,
            double elementSizeMin, ref HTuple model)
        {
            HOperatorSet.CreateBarCodeModel(new HTuple(), new HTuple(), out model);

            // 多线投票：扫描多行条线后按权重投票，抗局部污损/反光（课堂案例核心参数）
            HOperatorSet.SetBarCodeParam(model, "majority_voting", "true");
            // 找够即停，避免全图穷举（节拍）
            if (maxCount > 0)
                HOperatorSet.SetBarCodeParam(model, "stop_after_result_num", maxCount);
            // 条宽像素范围：小码/远距离码必须放宽下限，否则找不到（默认值偏大）
            if (elementSizeMin > 0)
            {
                HOperatorSet.SetBarCodeParam(model, "element_size_min", elementSizeMin);
                // 上限给 10 倍裕量：过大上限只影响搜索耗时，不会误检
                HOperatorSet.SetBarCodeParam(model, "element_size_max", elementSizeMin * 10);
            }

            // 码制：Auto → 'auto'（多码制通用但更慢、可靠性略降，课堂案例原话）
            //       显式指定 → 用 HALCON 标准码制名（准确性更好）
            string halconType = MapToHalcon1D(kind);

            HObject regions = null;
            HTuple dataStrings = null;
            try
            {
                HOperatorSet.FindBarCode(work, out regions, model, halconType, out dataStrings);

                int count = regions != null && regions.IsInitialized() ? CountObj(regions) : 0;
                if (count == 0)
                    return Fail($"未找到 {halconType} 码（检查 码制是否选错 / 条宽 element_size_min 是否偏大 / 对比度不足）");

                string text = JoinStrings(dataStrings);

                // 回读实际码制（Auto 模式下尤其有用：告诉调用方"这其实是 Code 128"）
                string decodedType = halconType;
                if (string.Equals(halconType, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        HOperatorSet.GetBarCodeResult(model, "all", "decoded_types", out HTuple types);
                        var t = JoinStrings(types);
                        if (!string.IsNullOrWhiteSpace(t)) decodedType = t;
                    }
                    catch { /* 部分码制不支持回读，保持 'auto' 即可 */ }
                }

                return new BarcodeReadResult
                {
                    Success = true,
                    TextResult = text,
                    BarcodeRegion = regions,
                    DecodedType = decodedType,
                    Count = count,
                    Message = $"识别成功 [{decodedType}] ×{count}"
                };
            }
            catch
            {
                regions?.Dispose();
                throw;
            }
        }

        // ==================================================================
        // 二维码（含 Data Matrix / Aztec / PDF417）
        // ==================================================================
        private static BarcodeReadResult Read2D(HObject work, BarcodeKind kind, int maxCount,
            string polarity, ref HTuple model)
        {
            string halconType = MapToHalcon2D(kind);

            // 创建参数：Data Matrix 的 ECC 200 需显式拼 'Data Matrix ECC 200'
            // ★ micro_qr 既非 create 参数也非 set 参数（HALCON 24.11 下两者都抛 #8831），
            //   实测强设会连标准 QR 一起打死 ⇒ 不设。Micro QR 本版模型不支持，属已知边界。
            HOperatorSet.CreateDataCode2dModel(halconType, new HTuple(), new HTuple(), out model);

            // 极性：'dark_on_light'（黑码白底，常见）/ 'light_on_dark'（白码黑底）/ 'any'
            // ★ 各码制支持的参数集合不同（PDF417 不接受 module_size_min/polarity 等，
            //    强设会抛 HALCON #8831 Unknown parameter name）。因此逐参数「尽力而为」，
            //    失败即静默跳过，绝不因单个参数让整次识别失败。
            TrySetDataCode2dParam(model, "polarity", polarity?.ToLowerInvariant());

            // 小码识别：模块尺寸下限放宽（默认偏保守）；仅对支持的码制生效
            TrySetDataCode2dParam(model, "module_size_min", 2);

            HObject symbols = null;
            HTuple resultHandles = null, dataStrings = null;
            try
            {
                HOperatorSet.FindDataCode2d(work, out symbols, model, new HTuple(), new HTuple(),
                    out resultHandles, out dataStrings);

                int count = dataStrings != null ? dataStrings.Length : 0;
                if (count == 0)
                    return Fail($"未找到 {halconType} 码（检查 极性 polarity 是否反了（白码黑底需 light_on_dark）/ 模块尺寸是否过小 / 有无透视畸变）");

                // maxCount 截断（HALCON 无 stop_after_result_num for 2D）
                var texts = new System.Collections.Generic.List<string>();
                for (int i = 0; i < count; i++)
                {
                    if (maxCount > 0 && texts.Count >= maxCount) break;
                    texts.Add(FixHalconString(dataStrings[i].S ?? string.Empty));
                }

                return new BarcodeReadResult
                {
                    Success = true,
                    TextResult = string.Join(" | ", texts),
                    BarcodeRegion = symbols,
                    DecodedType = halconType,
                    Count = texts.Count,
                    Message = $"识别成功 [{halconType}] ×{texts.Count}"
                };
            }
            catch
            {
                symbols?.Dispose();
                throw;
            }
        }

        // ==================================================================
        // 码制名映射（HALCON 标准字符串，大小写/空格敏感）
        // ==================================================================

        /// <summary>一维码：节点枚举 → HALCON find_bar_code 的 CodeType 字符串。</summary>
        private static string MapToHalcon1D(BarcodeKind kind)
        {
            switch (kind)
            {
                case BarcodeKind.Code128: return "Code 128";
                case BarcodeKind.Code39: return "Code 39";
                case BarcodeKind.EAN13: return "EAN-13";
                case BarcodeKind.UPC: return "UPC-A";
                default: return "auto";
            }
        }

        /// <summary>二维码：节点枚举 → HALCON create_data_code_2d_model 的 SymbolType 字符串。</summary>
        private static string MapToHalcon2D(BarcodeKind kind)
        {
            switch (kind)
            {
                case BarcodeKind.QRCode: return "QR Code";
                case BarcodeKind.DataMatrix: return "Data Matrix ECC 200";
                case BarcodeKind.Aztec: return "Aztec Code";
                case BarcodeKind.PDF417: return "PDF417";
                default: return "QR Code";
            }
        }

        // ==================================================================
        // 辅助
        // ==================================================================

        /// <summary>
        /// 尽力设置 2D 数据码参数：各码制可设参数集合不同（如 PDF417 不接受
        /// module_size_min / polarity，强设抛 HALCON #8831）。
        /// ★ HALCON 24.11 没有「列出该模型支持哪些参数名」的查询
        ///   （get_data_code_2d_param 的 'name' 分支同样抛 #8831），
        ///   故只能「试设 + 吞掉 HOperatorException」，绝不因单个参数让整次识别失败。
        /// </summary>
        private static void TrySetDataCode2dParam(HTuple model, string name, object value)
        {
            if (value == null) return;
            if (value is string s && string.IsNullOrWhiteSpace(s)) return;

            try
            {
                HTuple v = value is int i2 ? new HTuple(i2)
                         : value is double d ? new HTuple(d)
                         : new HTuple(value.ToString());
                HOperatorSet.SetDataCode2dParam(model, name, v);
            }
            catch (HalconDotNet.HOperatorException)
            {
                // 码制不支持该参数 ⇒ 忽略；不影响主识别链路
            }
        }

        /// <summary>
        /// 修复 halcondotnet 的字符串解码错位：HALCON 结果里的 UTF-8 字节被包装层按
        /// 系统 ANSI 代码页(Latin-1)逐个解码，中文/非 ASCII 变成 "ç¿°åº­" 这类乱码。
        /// 判据：整串所有字符都 ≤ U+00FF（纯 Latin-1 域）且能按 [Latin-1→UTF-8] 还原为
        /// 合法 UTF-8 且还原后含非 ASCII ⇒ 视为错位并还原；否则原样返回（纯 ASCII 码
        /// 不受影响，真正含 U+0100+ 的串也不会被误改）。
        /// </summary>
        private static string FixHalconString(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;

            // 含任何 > U+00FF 的字符 ⇒ 已是正确的 .NET 字符串，不动
            foreach (char c in s)
                if (c > '\u00FF') return s;

            // 纯 ASCII ⇒ 无需修复
            bool hasHigh = false;
            foreach (char c in s)
                if (c > '\u007F') { hasHigh = true; break; }
            if (!hasHigh) return s;

            try
            {
                var bytes = new byte[s.Length];
                for (int i = 0; i < s.Length; i++) bytes[i] = (byte)s[i];

                // 严格 UTF-8 解码：非法序列会抛，正好当作"不是这种错位"
                string fixedStr = new System.Text.UTF8Encoding(false, true).GetString(bytes);

                // 还原后应仍是"看起来更合理"的串：必须出现非 ASCII（否则说明本来就不是错位）
                bool fixedHasNonAscii = false;
                foreach (char c in fixedStr)
                    if (c > '\u007F') { fixedHasNonAscii = true; break; }
                if (!fixedHasNonAscii) return s;

                return fixedStr;
            }
            catch
            {
                return s;   // 还原失败 ⇒ 原样返回（宁可显示乱码，也不篡改数据）
            }
        }

        private static int CountObj(HObject obj)
        {
            try
            {
                HOperatorSet.CountObj(obj, out HTuple n);
                return n.Length > 0 ? n[0].I : 0;
            }
            catch { return 0; }
        }

        private static string JoinStrings(HTuple tuple)
        {
            if (tuple == null || tuple.Length == 0) return string.Empty;
            var list = new System.Collections.Generic.List<string>(tuple.Length);
            for (int i = 0; i < tuple.Length; i++) list.Add(FixHalconString(tuple[i].S ?? string.Empty));
            return string.Join(" | ", list);
        }

        private static BarcodeReadResult Fail(string msg)
            => new BarcodeReadResult { Success = false, Message = msg };
    }
}
