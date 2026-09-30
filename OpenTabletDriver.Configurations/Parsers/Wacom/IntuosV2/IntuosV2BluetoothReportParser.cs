using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using OpenTabletDriver.Plugin.Tablet;

namespace OpenTabletDriver.Configurations.Parsers.Wacom.IntuosV2
{
    /// <summary>
    /// Parses Bluetooth reports of the Wacom IntuosV2-over-BT protocol.
    /// Each 0x81 report carries up to 4 sub-packets of 8 bytes; every valid
    /// sub-packet is an independent, chronologically ordered position sample
    /// (measured ~134 Hz total on CTL-6100WL). All valid samples are exposed
    /// via <see cref="ParseAll"/> so the device reader can pace them out.
    /// </summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    public class IntuosV2BluetoothReportParser : IBatchReportParser<IDeviceReport>
    {
        private const int MinimumReportLength = 46;
        private const int SubPacketSize = 8;
        private const int SubPacketStart = 1;
        private const int MaxSubPackets = 4;

        private Vector2 _lastPosition = Vector2.Zero;

        /// <summary>官方同款平滑链（自适应深度滑动平均 + 减阈值死区滞回），解析层应用。</summary>
        private readonly WacomParitySmoothing smoothing = new();

        public IDeviceReport Parse(byte[] data)
        {
            IDeviceReport last = new DeviceReport(data);
            foreach (var report in ParseAll(data))
                last = report;
            return last;
        }

        public IEnumerable<IDeviceReport> ParseAll(byte[] data)
        {
            if (data.Length < MinimumReportLength || data[0] != 0x81)
            {
                yield return new DeviceReport(data);
                yield break;
            }

            bool anyValid = false;
            for (int i = 0; i < MaxSubPackets; i++)
            {
                int offset = SubPacketStart + i * SubPacketSize;
                if (!data[offset].IsBitSet(7))
                    continue;

                anyValid = true;
                var status = data[offset];
                if (!status.IsBitSet(6))
                {
                    // Proximity bit clear: pen is out of range at this sample.
                    smoothing.Reset();
                    yield return new OutOfRangeReport(data);
                    continue;
                }

                var report = new IntuosV2BluetoothReport(data, offset);
                if (status.IsBitSet(5))
                    _lastPosition = report.Position;
                else
                    report.Position = _lastPosition;

                // 官方同款平滑链：仅对真实位置样本生效（平板 counts 域）
                if (status.IsBitSet(5))
                {
                    var (sx, sy) = smoothing.Filter(report.Position.X, report.Position.Y, (int)report.HoverDistance);
                    _lastPosition = new Vector2((float)sx, (float)sy);
                    report.Position = _lastPosition;
                }
                yield return report;
            }

            if (!anyValid)
                yield return new IntuosV2BluetoothAuxReport(data);
        }
    }
}
