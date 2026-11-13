using System;
using System.Diagnostics;
using nanoFramework.Hardware.Esp32;

namespace IotStarter.Firmware.Diagnostics
{
    /// <summary>
    /// Debug output. Never pass a passphrase or an API token to any method here: callers log
    /// only lengths, SSIDs and error codes.
    /// </summary>
    public static class Log
    {
        public static void Info(string message)
        {
            Write("INF", message);
        }

        public static void Warn(string message)
        {
            Write("WRN", message);
        }

        public static void Error(string message, Exception ex)
        {
            Write("ERR", ex == null ? message : message + ": " + ex.GetType().Name + ": " + ex.Message);
        }

        /// <summary>Logs managed heap and internal native heap free bytes.</summary>
        public static void Memory(string label)
        {
            try
            {
                // Run(false) collects without compaction and returns the free managed heap in bytes.
                uint managedFree = nanoFramework.Runtime.Native.GC.Run(false);

                uint total;
                uint free;
                uint largest;
                NativeMemory.GetMemoryInfo(NativeMemory.MemoryType.Internal, out total, out free, out largest);

                Write("MEM", label + ": managed free=" + managedFree.ToString()
                    + " native internal free=" + free.ToString()
                    + " largest block=" + largest.ToString()
                    + " total=" + total.ToString());
            }
            catch (Exception ex)
            {
                Write("MEM", label + ": unavailable (" + ex.Message + ")");
            }
        }

        private static void Write(string level, string message)
        {
            long seconds = Environment.TickCount64 / 1000;
            Debug.WriteLine("[" + seconds.ToString() + "s " + level + "] " + message);
        }
    }
}
