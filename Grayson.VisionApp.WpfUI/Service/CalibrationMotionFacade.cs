//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationMotionFacade.cs
// 说 明: 标定域"低速到位"共享驱动门面（2026-09-05 P3 校验台）。
//        语义与 CalibrationWizardViewModel.MovePlatformTo/WaitAxesIdle/MoveZTo 对齐：
//        连接检查 → XY 双轴 MoveAbsolute(逐轴检拒) → WaitAxesIdle(8s 轮询/降级/稳定)，
//        Z 升降同语义。T3 决议："把 MovePlatformTo/WaitAxesIdle 示范收成可复用门面供
//        校验台调"。向导内部仍保留自身实现（避免 3000+ 行深耦合回归），方法头注释指向
//        本门面，后续重构向导采样驱动可切换到本类（单一实现）。
//        安全边界：验收场景不自动归零旋转轴（Epson U 归零语义只在向导九点平移前做），
//        调用方保证到位前姿态安全；速度默认低速，调用方可显式传入。
//===================================================================================
using System;
using System.Diagnostics;
using System.Threading;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;

namespace Grayson.Vision.WpfUI.Service
{
    /// <summary>
    /// 标定校验台运动门面：绑定一张运动控制卡 + 轴槽位，提供低速安全到位原语。
    /// 无运动卡时（null）所有动作为"纯演示模式"——Move* 返回 Ok 并记日志（与向导一致）。
    /// </summary>
    public class CalibrationMotionFacade
    {
        private readonly IMotionCard _motion;
        private readonly Action<string> _log;
        private readonly int _xAxis;
        private readonly int _yAxis;
        private readonly int _zAxis;
        private readonly int _uAxis;   // 旋转轴槽位（U 轴）；<0 表示未绑定
        private readonly float _speed;

        /// <summary>最近一次动作错误描述（调用方 UI 提示用）</summary>
        public string LastError { get; private set; }

        public CalibrationMotionFacade(IMotionCard motion, int xAxis, int yAxis, int zAxis,
            int uAxis = -1, float speed = 50f, Action<string> log = null)
        {
            _motion = motion;
            _xAxis = xAxis;
            _yAxis = yAxis;
            _zAxis = zAxis;
            _uAxis = uAxis;
            _speed = speed > 0 ? speed : 50f; // 速度 0 巨坑（ZMC 铁律）：<=0 时轴不动 IDLE 恒 0
            _log = log ?? (_ => { });
        }

        private void Log(string msg) => _log?.Invoke(msg);

        /// <summary>确保运动卡已连接（未连接则 Connect；null 卡=演示模式直接 Ok）</summary>
        public Result EnsureConnected()
        {
            if (_motion == null)
            {
                LastError = null;
                return Result.Ok();
            }
            if (_motion.State == DeviceState.Connected)
            {
                return Result.Ok();
            }
            var c = _motion.Connect();
            if (c != null && !c.Success)
            {
                LastError = c.Message;
                Log("[校验] 运动卡连接失败: " + c.Message);
            }
            return c ?? Result.Fail("Connect 返回 null");
        }

        /// <summary>
        /// 低速安全到位：XY 双轴 MoveAbsolute（逐轴检拒：超行程/范围钳制必须中止，防错误位置采图）
        /// → 等待到位。返回是否真的到位；被拒返回 false 且 LastError 给出原因。
        /// </summary>
        public bool MoveToXY(double worldX, double worldY)
        {
            if (_motion == null)
            {
                Log($"[校验] 未绑定运动卡（演示模式），跳过到位 X={worldX:F1},Y={worldY:F1}。");
                return true;
            }
            var conn = EnsureConnected();
            if (!conn.Success)
            {
                LastError = conn.Message;
                return false;
            }

            var rx = _motion.MoveAbsolute(_xAxis, (float)worldX, _speed);
            var ry = _motion.MoveAbsolute(_yAxis, (float)worldY, _speed);
            if ((rx != null && !rx.Success) || (ry != null && !ry.Success))
            {
                // 2026-09-06：保留控制器原始反馈（含错误码 4001/4007/2997），不再写死"超行程/安全范围？"误导
                LastError = "到位被拒: " +
                            (rx != null && !rx.Success ? rx.Message : "") +
                            (ry != null && !ry.Success ? " / " + ry.Message : "");
                Log($"[校验] 低速到位被拒 X={worldX:F3},Y={worldY:F3}：{LastError}");
                return false;
            }
            WaitAxesIdle(_xAxis, _yAxis);
            return true;
        }

        /// <summary>Z 轴升降（SafeZ 抬升/下探；MoveAbsolute + 等待到位）</summary>
        public bool MoveToZ(double z)
        {
            if (_motion == null)
            {
                Log($"[校验] 未绑定运动卡（演示模式），跳过 Z 升降到 {z:F1}。");
                return true;
            }
            var conn = EnsureConnected();
            if (!conn.Success)
            {
                LastError = conn.Message;
                return false;
            }
            var rz = _motion.MoveAbsolute(_zAxis, (float)z, _speed);
            if (rz != null && !rz.Success)
            {
                LastError = "Z 升降被拒";
                Log($"[校验] Z 轴到 {z:F1} 被拒: {rz.Message}");
                return false;
            }
            WaitAxesIdle(_zAxis);
            return true;
        }

        /// <summary>相对步进（P4 对针 JOG）：读当前 X/Y 反馈 + 增量 → 低速绝对到位。</summary>
        public bool MoveBy(double dx, double dy)
        {
            if (_motion == null)
            {
                Log($"[步进] 未绑定运动卡（演示模式），跳过 dx={dx:F2},dy={dy:F2}。");
                return true;
            }
            var fx = _motion.GetFeedbackPosition(_xAxis);
            var fy = _motion.GetFeedbackPosition(_yAxis);
            if (!fx.Success || !fy.Success)
            {
                LastError = "读取当前位置失败，无法相对步进";
                Log("[步进] " + LastError + (fx.Success ? "" : " X:" + fx.Message) + (fy.Success ? "" : " Y:" + fy.Message));
                return false;
            }
            return MoveToXY(fx.Data + dx, fy.Data + dy);
        }

        /// <summary>Z 轴相对步进（P4 对针 JOG）：读当前 Z 反馈 + 增量 → 低速绝对到位。</summary>
        public bool MoveByZ(double dz)
        {
            if (_motion == null)
            {
                Log($"[步进] 未绑定运动卡（演示模式），跳过 dz={dz:F2}。");
                return true;
            }
            var fz = _motion.GetFeedbackPosition(_zAxis);
            if (!fz.Success)
            {
                LastError = "读取 Z 当前位置失败，无法相对步进";
                Log("[步进] " + LastError + " Z:" + fz.Message);
                return false;
            }
            return MoveToZ(fz.Data + dz);
        }

        /// <summary>U（旋转）轴相对步进（P4 对针 JOG）：读当前 U 反馈 + 增量 → 低速绝对到位。</summary>
        public bool MoveByU(double du)
        {
            if (_uAxis < 0)
            {
                LastError = "未绑定旋转轴（U 轴槽位未配置）";
                Log("[步进] " + LastError);
                return false;
            }
            if (_motion == null)
            {
                Log($"[步进] 未绑定运动卡（演示模式），跳过 du={du:F2}°。");
                return true;
            }
            var fu = _motion.GetFeedbackPosition(_uAxis);
            if (!fu.Success)
            {
                LastError = "读取 U 当前位置失败，无法相对步进";
                Log("[步进] " + LastError + " U:" + fu.Message);
                return false;
            }
            return MoveToU(fu.Data + du);
        }

        /// <summary>U（旋转）轴绝对到位（MoveAbsolute + 等待到位）</summary>
        public bool MoveToU(double u)
        {
            if (_uAxis < 0)
            {
                LastError = "未绑定旋转轴（U 轴槽位未配置）";
                Log("[步进] " + LastError);
                return false;
            }
            if (_motion == null)
            {
                Log($"[步进] 未绑定运动卡（演示模式），跳过 U 到位 {u:F1}°。");
                return true;
            }
            var conn = EnsureConnected();
            if (!conn.Success)
            {
                LastError = conn.Message;
                return false;
            }
            var ru = _motion.MoveAbsolute(_uAxis, (float)u, _speed);
            if (ru != null && !ru.Success)
            {
                LastError = "U 轴到位被拒: " + ru.Message;
                Log($"[步进] U 轴到 {u:F1}° 被拒: {ru.Message}");
                return false;
            }
            WaitAxesIdle(_uAxis);
            return true;
        }

        /// <summary>读取指定轴当前反馈位置</summary>
        public Result<float> GetAxisFeedback(int axis)
        {
            if (_motion == null)
            {
                return Result<float>.Ok(0f);
            }
            return _motion.GetFeedbackPosition(axis);
        }

        /// <summary>
        /// 设置数字输出（真空阀 / 吹气阀等）。P4 对针/点哪去哪校验的「吸住」动作共用。
        /// ioNum 由调用方从业务配置/标定档案传入（CalibrationProfile.PickVacuumIoIndex）。
        /// </summary>
        public bool SetOutput(int ioNum, bool state)
        {
            if (_motion == null)
            {
                Log($"[吸嘴] 未绑定运动卡（演示模式），跳过 IO{ioNum} {(state ? "ON" : "OFF")}。");
                return true;
            }
            var conn = EnsureConnected();
            if (!conn.Success)
            {
                LastError = conn.Message;
                return false;
            }
            var r = _motion.SetOutput(ioNum, state);
            if (r != null && !r.Success)
            {
                LastError = "设置输出被拒: " + r.Message;
                Log($"[吸嘴] IO{ioNum} {(state ? "ON" : "OFF")} 被拒: {r.Message}");
                return false;
            }
            return true;
        }

        /// <summary>
        /// 轮询等待指定轴全部空闲（到位），到位后留 100ms 稳定时间。
        /// 不支持 IsAxisIdle 查询（返回失败）或超时（8s）时降级固定等待并记日志。
        /// </summary>
        public void WaitAxesIdle(params int[] axes)
        {
            if (_motion == null || axes == null || axes.Length == 0)
            {
                return;
            }
            const int timeoutMs = 8000;
            try
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    bool allIdle = true;
                    foreach (var axis in axes)
                    {
                        var idleRes = _motion.IsAxisIdle(axis);
                        if (!idleRes.Success)
                        {
                            Log($"[校验] 运动卡不支持轴 [{axis}] 到位查询，降级固定等待 500ms。");
                            Thread.Sleep(500);
                            return;
                        }
                        if (!idleRes.Data)
                        {
                            allIdle = false;
                            break;
                        }
                    }
                    if (allIdle)
                    {
                        Thread.Sleep(100); // 到位后稳定片刻（消除减速末端残余震荡）
                        return;
                    }
                    Thread.Sleep(20);
                }
                Log($"[校验] 等待轴({string.Join("/", axes)})到位超时({timeoutMs}ms)，继续。");
            }
            catch (Exception ex)
            {
                Log("[校验] WaitAxesIdle 异常: " + ex.Message);
            }
        }
    }
}
