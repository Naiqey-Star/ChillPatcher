using System;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace ChillPatcher.Rime
{
    internal static class RimeNativeLoader
    {
        private static IntPtr module;
        private static readonly object gate = new object();
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
        public static void EnsureLoaded()
        {
            lock (gate)
            {
                if (module != IntPtr.Zero) return;
                string path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(RimeNativeLoader).Assembly.Location), "rime.dll"));
                if (!File.Exists(path)) throw new FileNotFoundException("Rime native library missing", path);
                // Search dependencies beside this DLL and in default trusted directories.
                module = LoadLibraryExW(path, IntPtr.Zero, 0x100 | 0x1000);
                if (module == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot load Rime or its dependencies: " + path);
                // Keep the handle for process lifetime; Rime owns background threads.
            }
        }
    }
}
