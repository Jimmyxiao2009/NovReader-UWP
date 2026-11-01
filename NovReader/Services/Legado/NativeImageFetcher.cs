using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using NovReader.Services.Legado.Js;

namespace NovReader.Services.Legado
{
    public static class NativeImageFetcher
    {
        public static Func<string> CertificatePathProvider;
        private static readonly object Gate = new object();
        private static bool _tried;
        private static FetchFunction _fetch;
        private static FreeFunction _free;
        private static IntPtr _module;
        private static string _certificatePem;

        [DllImport("api-ms-win-core-libraryloader-l2-1-0.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr LoadPackagedLibrary(string name, uint reserved);
        [DllImport("api-ms-win-core-libraryloader-l1-2-0.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int FetchFunction(IntPtr url, IntPtr headers, IntPtr ca, int timeout, out IntPtr data, out int length, IntPtr error, int capacity);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FreeFunction(IntPtr data);

        private static bool EnsureLoaded()
        {
            lock (Gate)
            {
                if (_tried) return _fetch != null;
                _tried = true;
                try
                {
                    _module = LoadPackagedLibrary("NetBridge.dll", 0);
                    if (_module == IntPtr.Zero) throw new Exception("LoadPackagedLibrary NetBridge: " + Marshal.GetLastWin32Error());
                    IntPtr fetch = GetProcAddress(_module, "NetBridgeFetch");
                    IntPtr free = GetProcAddress(_module, "NetBridgeFree");
                    if (fetch == IntPtr.Zero || free == IntPtr.Zero) throw new Exception("NetBridge exports missing");
                    _free = Marshal.GetDelegateForFunctionPointer<FreeFunction>(free);
                    _fetch = Marshal.GetDelegateForFunctionPointer<FetchFunction>(fetch);
                }
                catch (Exception error) { JscBackend.TraceStage("native image transport unavailable: " + error.Message); }
                return _fetch != null;
            }
        }

        public static Task<byte[]> FetchAsync(string url, string headers, int timeout)
        {
            return Task.Run(delegate { return Fetch(url, headers, timeout); });
        }

        private static IntPtr Utf8(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes((text ?? "") + "\0");
            IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            return pointer;
        }

        private static byte[] Fetch(string url, string headers, int timeout)
        {
            if (!EnsureLoaded()) return null;
            IntPtr urlPtr = IntPtr.Zero, headerPtr = IntPtr.Zero, caPtr = IntPtr.Zero, errorPtr = IntPtr.Zero, data = IntPtr.Zero;
            try
            {
                var lines = new StringBuilder();
                foreach (var header in HttpFetcher.ParseHeaders(headers)) lines.Append(header.Key).Append(": ").Append(header.Value).Append('\n');
                urlPtr = Utf8(url);
                headerPtr = Utf8(lines.ToString());
                lock (Gate)
                {
                    if (_certificatePem == null)
                        _certificatePem = System.IO.File.ReadAllText(CertificatePathProvider());
                }
                caPtr = Utf8(_certificatePem);
                errorPtr = Marshal.AllocHGlobal(4096);
                int length;
                int result = _fetch(urlPtr, headerPtr, caPtr, timeout, out data, out length, errorPtr, 4096);
                byte[] errorBytes = new byte[4096];
                Marshal.Copy(errorPtr, errorBytes, 0, errorBytes.Length);
                int end = Array.IndexOf(errorBytes, (byte)0);
                JscBackend.TraceStage("native image transport: " + Encoding.UTF8.GetString(errorBytes, 0, end < 0 ? errorBytes.Length : end));
                if (result != 0 || data == IntPtr.Zero || length <= 0 || length > 32 * 1024 * 1024) return null;
                var bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, length);
                return bytes;
            }
            catch (Exception error) { JscBackend.TraceStage("native image transport exception: " + error.Message); return null; }
            finally
            {
                if (data != IntPtr.Zero) _free(data);
                Marshal.FreeHGlobal(urlPtr); Marshal.FreeHGlobal(headerPtr); Marshal.FreeHGlobal(caPtr); Marshal.FreeHGlobal(errorPtr);
            }
        }
    }
}
