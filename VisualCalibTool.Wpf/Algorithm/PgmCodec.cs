using System;
using System.Globalization;
using System.Text;

namespace VisualCalibTool.Algorithm
{
    /// <summary>
    /// 灰度帧的 PGM（P5 二进制）编解码。
    ///
    /// ★ 为什么留档不用裸 <c>.bin</c>、也不急着引第三方图像库：
    ///   采样帧是"复盘时唯一的物证"（历史上"究竟是算法错还是图错"的争论只能靠图终结）。
    ///   裸 bin 一旦丢了宽高参数就成废数据；PGM 头自带宽高 → <b>十年后还能被 HALCON /
    ///   Python / ImageJ 直接打开</b>，而且编解码是纯计算，可离线单测（编码→解码→逐字节相同）。
    ///
    /// 格式（P5）：魔数 "P5"、宽、高、最大灰度 255，然后是按行排列的原始灰度字节。
    /// 头部的空白允许包含 <c>#</c> 注释行 —— 解码时按 PGM 规范跳过。
    /// </summary>
    public static class PgmCodec
    {
        public const int MaxDimension = 100000;

        /// <summary>编码为 P5 PGM。<paramref name="gray"/> 长度必须等于 width × height。</summary>
        public static byte[] Encode(byte[] gray, int width, int height)
        {
            if (gray == null || width <= 0 || height <= 0)
            {
                return null;
            }

            if (gray.Length < width * height)
            {
                return null;
            }

            string header = string.Format(
                CultureInfo.InvariantCulture, "P5\n{0} {1}\n255\n", width, height);
            byte[] head = Encoding.ASCII.GetBytes(header);

            byte[] outBytes = new byte[head.Length + width * height];
            Buffer.BlockCopy(head, 0, outBytes, 0, head.Length);
            Buffer.BlockCopy(gray, 0, outBytes, head.Length, width * height);
            return outBytes;
        }

        /// <summary>只读头（不解码像素），用于"先看这张留档多大、能不能用"。</summary>
        public static bool TryReadInfo(byte[] bytes, out int width, out int height, out int pixelOffset)
        {
            width = 0;
            height = 0;
            pixelOffset = 0;

            if (bytes == null || bytes.Length < 8)
            {
                return false;
            }

            int i = 0;

            // 魔数
            if (bytes[0] != (byte)'P' || bytes[1] != (byte)'5')
            {
                return false;
            }

            i = 2;

            if (!ReadInt(bytes, ref i, out width))
            {
                return false;
            }

            if (!ReadInt(bytes, ref i, out height))
            {
                return false;
            }

            int maxVal;
            if (!ReadInt(bytes, ref i, out maxVal))
            {
                return false;
            }

            if (maxVal <= 0 || maxVal > 255)
            {
                return false;
            }

            if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension)
            {
                return false;
            }

            // 头与像素之间规范要求恰好一个空白字符
            if (i < bytes.Length && IsSpace(bytes[i]))
            {
                i++;
            }

            pixelOffset = i;
            return true;
        }

        /// <summary>解码 P5 PGM。</summary>
        public static bool TryDecode(byte[] bytes, out byte[] gray, out int width, out int height)
        {
            gray = null;
            width = 0;
            height = 0;

            int offset;
            if (!TryReadInfo(bytes, out width, out height, out offset))
            {
                return false;
            }

            int need = width * height;
            if (bytes.Length - offset < need)
            {
                return false;
            }

            gray = new byte[need];
            Buffer.BlockCopy(bytes, offset, gray, 0, need);
            return true;
        }

        private static bool ReadInt(byte[] bytes, ref int i, out int value)
        {
            value = 0;

            // 跳过空白与注释
            while (i < bytes.Length)
            {
                byte b = bytes[i];
                if (b == (byte)'#')
                {
                    while (i < bytes.Length && bytes[i] != (byte)'\n')
                    {
                        i++;
                    }
                }
                else if (IsSpace(b))
                {
                    i++;
                }
                else
                {
                    break;
                }
            }

            int start = i;
            long acc = 0;
            while (i < bytes.Length && bytes[i] >= (byte)'0' && bytes[i] <= (byte)'9')
            {
                acc = acc * 10 + (bytes[i] - (byte)'0');
                if (acc > int.MaxValue)
                {
                    return false;
                }

                i++;
            }

            if (i == start)
            {
                return false;
            }

            value = (int)acc;
            return true;
        }

        private static bool IsSpace(byte b)
        {
            return b == (byte)' ' || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n'
                   || b == (byte)'\v' || b == (byte)'\f';
        }
    }
}
