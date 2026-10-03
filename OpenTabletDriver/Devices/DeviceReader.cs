using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Devices;
using OpenTabletDriver.Plugin.Tablet;

namespace OpenTabletDriver.Devices
{
    public class DeviceReader<T> : IDisposable where T : IDeviceReport
    {
        public DeviceReader(IDeviceEndpoint endpoint, IReportParser<T> reportParser)
        {
            Endpoint = endpoint;
            Parser = reportParser ?? throw new ArgumentNullException(nameof(reportParser));
            workerThread = new Thread(Main)
            {
                Name = "OpenTabletDriver Device Reader",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
        }

        private readonly Thread workerThread;
        private bool initialized, connected;

        /// <summary>
        /// The device endpoint in which is reporting data in the <see cref="ReportStream"/>.
        /// </summary>
        public IDeviceEndpoint Endpoint { protected set; get; }

        /// <summary>
        /// The raw device endpoint report stream.
        /// </summary>
        public IDeviceEndpointStream? ReportStream { protected set; get; }

        /// <summary>
        /// The <see cref="IReportParser{T}"/> in which the device reports will be parsed with.
        /// </summary>
        public IReportParser<T> Parser { private set; get; }

        /// <summary>
        /// Whether to make an extra cloned report with data left unmodified.
        /// </summary>
        public bool RawClone { set; get; }

        /// <summary>
        /// Invoked when a new report comes in from the device.
        /// </summary>
        public event EventHandler<T>? Report;

        /// <summary>
        /// Invoked when a new report comes in from the device.
        /// </summary>
        /// <remarks>
        /// This will only be invoked when <see cref="RawClone"/> is set to true.
        /// This report is not meant in any way to be modified, as it is supposed to represent the original data.
        /// </remarks>
        public event EventHandler<T>? RawReport;

        /// <summary>
        /// Whether the device is actively emitting reports and being parsed.
        /// </summary>
        public bool Connected
        {
            protected set
            {
                connected = value;
                ConnectionStateChanged?.Invoke(this, Connected);
            }
            get => connected;
        }

        /// <summary>
        /// Invoked when <see cref="Connected"/> is changed.
        /// </summary>
        public event EventHandler<bool>? ConnectionStateChanged;

        [MemberNotNullWhen(true, nameof(ReportStream))]
        protected virtual bool Initialize()
        {
            try
            {
                ReportStream = Endpoint.Open();
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                return false;
            }
        }

        protected virtual void Start()
        {
            if (!initialized)
                initialized = Initialize();

            if (initialized)
                workerThread.Start();
        }

        protected void Main()
        {
            // Only batch-report devices (Bluetooth tablets paced at a fixed rate)
            // need the time-constraint thread policy; USB and other devices keep
            // the stock scheduler behavior.
            if (Parser is IBatchReportParser<T>)
                MacOSRealtimeThread.Apply();
            try
            {
                Connected = true;
                while (Connected)
                {
                    var data = ReportStream!.Read();

                    if (Parser is IBatchReportParser<T> batchParser)
                    {
                        // Bluetooth tablets batch several chronological samples into
                        // one HID report. Pace them out at the tablet's true sample
                        // rate so downstream sees a continuous stream, like USB.
                        PaceBatch(batchParser.ParseAll(data));
                    }
                    else if (Parser.Parse(data) is T report)
                    {
                        OnReport(report);
                    }

                    // We create a clone of the report to avoid data being modified on the tablet debugger.
                    if (RawClone && RawReport != null && Parser.Parse(data) is T debugReport)
                        OnRawReport(debugReport);
                }
            }
            catch (ObjectDisposedException dex)
            {
                Log.Debug("Device", $"{(string.IsNullOrWhiteSpace(dex.ObjectName) ? "A device stream" : dex.ObjectName)} was disposed.");
            }
            catch (IOException ioex) when (ioex.Message == "I/O disconnected." || ioex.Message == "Operation failed after some time.")
            {
                Log.Write("Device", "Device disconnected.");
            }
            catch (ArgumentOutOfRangeException)
            {
                Log.Write("Device", "Not enough report data returned by the device. Was it disconnected?");
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
            finally
            {
                Connected = false;
            }
        }

        protected virtual void OnReport(T report) => Report?.Invoke(this, report);
        protected virtual void OnRawReport(T report) => RawReport?.Invoke(this, report);

        /// <summary>Stopwatch used for sample pacing and rate estimation.</summary>
        private readonly System.Diagnostics.Stopwatch paceWatch = System.Diagnostics.Stopwatch.StartNew();

        /// <summary>
        /// Inter-frame emission interval for batched (Bluetooth) reports.
        /// Matches the official Wacom driver's host-side spreading (WacSleep 3.7ms
        /// between sub-frames): drains a burst faster than the tablet produces
        /// samples (~7.5ms), keeping latency near zero and radio gaps under one
        /// display frame. See tmp/wacom-official/analysis-report.md.
        /// </summary>
        private const double PaceFrameMs = 3.7;

        /// <summary>Playback time of the last emitted sample (ms on <see cref="paceWatch"/>).</summary>
        private double paceLastEmitMs;

        // ---- 遥测：PaceBatch 发射滞后与批次规模，每 30s 输出一行（Log.Debug）----
        // 位置：本文件 PaceBatch() 末尾；输出目标：守护进程日志 Logs/*.json，Message 含 "[TELEMETRY] pacing:"
        // lagMax = 每帧实际发射时刻晚于计划时刻的最大值（ms）：调度饥饿/系统负载的直接证据
        private readonly System.Diagnostics.Stopwatch paceTeleWatch = System.Diagnostics.Stopwatch.StartNew();
        private double paceTeleLagMax;
        private int paceTeleBatchMax, paceTeleEmits, paceTeleBatches;

        private void PaceBatch(IEnumerable<T> reports)
        {
            int n = 0;
            foreach (var report in reports)
            {
                double target = Math.Max(paceWatch.Elapsed.TotalMilliseconds, paceLastEmitMs + PaceFrameMs);
                double wait = target - paceWatch.Elapsed.TotalMilliseconds;
                if (wait > 0.2)
                {
                    // Coarse sleep for the bulk of the wait, then spin for sub-ms precision.
                    var until = paceWatch.Elapsed.TotalMilliseconds + wait;
                    while (true)
                    {
                        double remaining = until - paceWatch.Elapsed.TotalMilliseconds;
                        if (remaining <= 0)
                            break;
                        if (remaining > 1.5)
                            Thread.Sleep(1);
                        else
                            Thread.SpinWait(50);
                    }
                }

                OnReport(report);
                double emitLag = paceWatch.Elapsed.TotalMilliseconds - target;
                if (emitLag > paceTeleLagMax) paceTeleLagMax = emitLag;
                paceTeleEmits++;
                paceLastEmitMs = Math.Max(paceWatch.Elapsed.TotalMilliseconds, target);
                n++;
            }

            if (n > paceTeleBatchMax) paceTeleBatchMax = n;
            paceTeleBatches++;
            if (paceTeleWatch.Elapsed.TotalSeconds >= 30)
            {
                Log.Debug("Device",
                    $"[TELEMETRY] pacing: lagMax={paceTeleLagMax:F1}ms batchMax={paceTeleBatchMax} " +
                    $"emits={paceTeleEmits} batches={paceTeleBatches}");
                paceTeleLagMax = 0;
                paceTeleBatchMax = paceTeleEmits = paceTeleBatches = 0;
                paceTeleWatch.Restart();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private bool _isDisposed;

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed) return;

            if (disposing)
            {
                Connected = false;
                ReportStream?.Dispose();
            }

            _isDisposed = true;
        }
    }
}
