using System;
using System.Runtime.InteropServices;
using OpenTabletDriver.Plugin;

namespace OpenTabletDriver.Devices
{
    /// <summary>
    /// Raises a thread to a time-constraint (realtime) scheduling policy on macOS,
    /// mirroring the official Wacom driver (thread_policy_set with period 4.878ms /
    /// 205Hz, computation 80µs, constraint 1ms). Keeps report pacing precise
    /// under system load. Best-effort: silently falls back to normal priority.
    /// </summary>
    internal static class MacOSRealtimeThread
    {
        private const int PolicyTimeConstraint = 2;
        private const int PolicyTimeConstraintCount = 4;

        public static void Apply()
        {
            try
            {
                var policy = new ThreadTimeConstraintPolicy
                {
                    Period = 4_878_000,     // 4.878 ms in ns ≈ 205 Hz
                    Computation = 80_000,   // 80 µs
                    Constraint = 1_000_000, // 1 ms
                    Preemptible = 1
                };
                var thread = pthread_mach_thread_np(pthread_self());
                if (thread_policy_set(thread, PolicyTimeConstraint, ref policy, PolicyTimeConstraintCount) == 0)
                    Log.Debug("Device", "Device reader thread raised to time-constraint policy (205 Hz).");
                else
                    Log.Debug("Device", "thread_policy_set returned an error; reader stays at normal priority.");
            }
            catch (Exception ex)
            {
                Log.Debug("Device", $"Unable to set thread policy: {ex.Message}");
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ThreadTimeConstraintPolicy
        {
            public uint Period;
            public uint Computation;
            public uint Constraint;
            public int Preemptible;
        }

        [DllImport("libSystem.dylib")]
        private static extern nint pthread_self();

        [DllImport("libSystem.dylib")]
        private static extern uint pthread_mach_thread_np(nint pthread);

        [DllImport("libSystem.dylib")]
        private static extern int thread_policy_set(uint thread, int policy, ref ThreadTimeConstraintPolicy data, int count);
    }
}
