using System;
using System.Collections.Generic;

namespace OpenTabletDriver.Configurations.Parsers.Wacom.IntuosV2
{
    /// <summary>
    /// Ports the official Wacom driver's default-enabled smoothing chain
    /// (reverse-engineered from WacomTabletDriver 6.4.14, see
    /// tmp/wacom-official/analysis-report.md), applied at the parse layer so
    /// downstream sees the same stream the official driver produces:
    ///
    ///   height moving-average → adaptive-depth X/Y moving-average
    ///   (CHeightVariableFilter) → subtract-threshold hysteresis deadzone.
    ///
    /// Design goal is "stabilize tremor, never lag fast strokes".
    /// All constants are tablet counts; tablet-space only.
    /// </summary>
    internal sealed class WacomParitySmoothing
    {
        // ---- 官方默认常量（可调） ----
        /// <summary>官方 //CoordinateFilterDepth：基准滤波深度（贴板时）。</summary>
        private const int BaseDepth = 4;
        /// <summary>官方 CTL 笔死区滞回阈值（counts）。</summary>
        private const double HysteresisCounts = 5;
        /// <summary>高度值滑动平均深度。</summary>
        private const int HeightDepth = 4;

        // ---- X/Y 自适应滑动平均 ----
        private readonly Queue<double> fifoX = new();
        private readonly Queue<double> fifoY = new();
        private double sumX, sumY;
        private int depth = BaseDepth;

        // ---- 高度平滑 ----
        private readonly Queue<int> fifoH = new();
        private int sumH;

        // ---- 输入位置（快速运动检测）与滞回输出 ----
        private double lastInX, lastInY;
        private bool hasInput;
        private double outX, outY;
        private bool hasOutput;

        /// <summary>笔离板/滤波上下文失效时清空全部状态。</summary>
        public void Reset()
        {
            fifoX.Clear(); fifoY.Clear(); fifoH.Clear();
            sumX = sumY = sumH = 0;
            depth = BaseDepth;
            hasInput = hasOutput = false;
        }

        // ---- 遥测：只观测，不参与滤波。每 30s 输出一行 [TELEMETRY]（Log.Debug）----
        // 位置：本文件 Filter() 末尾；输出目标：守护进程日志 Logs/*.json，Message 含 "[TELEMETRY] smoothing:"
        private readonly System.Diagnostics.Stopwatch _teleWatch = System.Diagnostics.Stopwatch.StartNew();
        private int _teleSamples, _teleFastResets, _teleSteady, _teleDepthSum;

        /// <summary>输入一个原始样本（平板 counts），返回平滑后位置。</summary>
        public (double X, double Y) Filter(double x, double y, int hoverDistance)
        {
            // 1) 高度平滑（0..63 → 0..255 尺度）
            fifoH.Enqueue(hoverDistance); sumH += hoverDistance;
            if (fifoH.Count > HeightDepth) sumH -= fifoH.Dequeue();
            int h = sumH / fifoH.Count;
            int h255 = Math.Min(255, h * 4);

            // 2) 目标深度 + 快速运动立即回浅
            //    抖动阈值 3+9·h/255：贴板 3 counts，满悬空 12 counts
            double jitter = 3 + 9.0 * h255 / 255.0;
            int target = (int)Math.Round(BaseDepth * (1 + 3.0 * h255 / 255.0)); // 1×..4× 基准
            if (hasInput)
            {
                double dx = x - lastInX, dy = y - lastInY;
                if (dx * dx + dy * dy > (2 * jitter) * (2 * jitter))
                {
                    depth = 1; // 快速运动：立即回浅，不拖影
                    _teleFastResets++;
                }
            }
            lastInX = x; lastInY = y; hasInput = true;

            // 非对称斜坡：加深 +2/样本，变浅 -1/样本
            if (depth < target) depth = Math.Min(target, depth + 2);
            else if (depth > target) depth = Math.Max(target, depth - 1);

            // 3) 深度 depth 的滑动平均
            fifoX.Enqueue(x); sumX += x;
            fifoY.Enqueue(y); sumY += y;
            if (fifoX.Count > depth) sumX -= fifoX.Dequeue();
            if (fifoY.Count > depth) sumY -= fifoY.Dequeue();
            double avgX = sumX / fifoX.Count;
            double avgY = sumY / fifoY.Count;

            // 4) 减阈值死区滞回：|d|>hyst → d -= sign·hyst，否则保持不动
            //    稳态收敛例外：输入静止（FIFO 跨度 ≤ 2×抖动阈值，即只剩手部微颤）
            //    时直通平均值。滞回死区会把渐进逼近冻结在间隙里（悬空深度16时
            //    可达 80 counts ≈ 13px），导致光标永远差一点贴不到屏幕边缘——
            //    Dock 自动浮现需要光标真正进入底部边缘区（实测证据见
            //    tmp/hidprobe/dockprobe4.c p7：减速停在13px外=不浮现）。
            if (!hasOutput)
            {
                outX = avgX; outY = avgY; hasOutput = true;
            }
            else if (Span(fifoX) <= 2 * jitter && Span(fifoY) <= 2 * jitter)
            {
                outX = avgX; outY = avgY;
                _teleSteady++;
            }
            else
            {
                double ox = avgX - outX, oy = avgY - outY;
                if (Math.Abs(ox) > HysteresisCounts) outX += ox - Math.Sign(ox) * HysteresisCounts;
                if (Math.Abs(oy) > HysteresisCounts) outY += oy - Math.Sign(oy) * HysteresisCounts;
            }
            _teleSamples++; _teleDepthSum += depth;
            if (_teleWatch.Elapsed.TotalSeconds >= 30)
            {
                OpenTabletDriver.Plugin.Log.Debug("Smoothing",
                    $"[TELEMETRY] smoothing: avgDepth={(_teleDepthSum / (double)Math.Max(1, _teleSamples)):F1} " +
                    $"depth={depth} target={target} h={h255} fastResets={_teleFastResets} " +
                    $"steadyBypass={_teleSteady} samples={_teleSamples}");
                _teleSamples = _teleFastResets = _teleSteady = _teleDepthSum = 0;
                _teleWatch.Restart();
            }
            return (outX, outY);
        }

        private static double Span(Queue<double> fifo)
        {
            double min = double.MaxValue, max = double.MinValue;
            foreach (var v in fifo)
            {
                if (v < min) min = v;
                if (v > max) max = v;
            }
            return max - min;
        }
    }
}
