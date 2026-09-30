using System.Collections.Generic;

namespace OpenTabletDriver.Plugin.Tablet
{
    /// <summary>
    /// A report parser whose raw device reports can contain multiple chronologically
    /// ordered samples (e.g. Bluetooth tablets batching several position samples into
    /// one HID report). The device reader paces these samples out at the tablet's
    /// true sample rate, restoring a continuous report stream.
    /// </summary>
    public interface IBatchReportParser<T> : IReportParser<T> where T : IDeviceReport
    {
        /// <summary>
        /// All reports contained in the raw device report, in chronological order.
        /// </summary>
        IEnumerable<T> ParseAll(byte[] data);
    }
}
