using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace NovReader.Services.Legado.Js
{
    /// <summary>
    /// JavaScriptCore 原生桥（JsBridge.dll，ARM32，静态链接 JSC/WTF，随包携带 ICU）。
    /// 求值时把 java.* 宿主函数经函数指针回调进 C#（同步 HTTP 在后台线程执行）。
    /// 回调约定：原生传入 UTF-8 入参和一个输出缓冲区，C# 写入结果返回字节数，缓冲区不够返回 -1。
    /// DLL 不在（x64 调试机）或加载失败时 IsAvailable=false，LegadoJs 会记成“JS 不可用”。
    /// </summary>
    public sealed class JscBackend : IJsBackend
    {
        private const string Dll = "JsBridge.dll";

        private static bool? _available;
        private static IntPtr _ctx;
        private static readonly object _gate = new object();
        private static JsHost _host;
        private static string _lastError = "";
        private static IntPtr _module;
        private static InitFunction _init;
        private static EvaluateFunction _evaluate;
        public static Func<string> DiagnosticPathProvider;

        /// <summary>同步落盘：原生 fastfail 不会等待 AppState 的异步日志。</summary>
        public static void TraceStage(string stage)
        {
            try
            {
                string path = DiagnosticPathProvider == null ? null : DiagnosticPathProvider();
                if (!String.IsNullOrEmpty(path))
                    System.IO.File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss") + " managed " + stage + Environment.NewLine);
            }
            catch (Exception) { }
        }

        public static string ReadDiagnosticLog()
        {
            try
            {
                string path = DiagnosticPathProvider == null ? null : DiagnosticPathProvider();
                if (String.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return "";
                using (var stream = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                {
                    if (stream.Length > 32768) stream.Seek(-32768, System.IO.SeekOrigin.End);
                    using (var reader = new System.IO.StreamReader(stream)) return reader.ReadToEnd();
                }
            }
            catch (Exception) { return ""; }
        }

        // ------------------------------------------------------------ 宿主回调（原生 JS 调进来）

        /// <summary>入参 utf8In；结果写入 bufOut（追加 NUL），返回字节数；缓冲区不够返回 -1。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NativeCallback(IntPtr utf8In, IntPtr buf, int cap);

        private static int WriteResult(Func<string> call, IntPtr buf, int cap)
        {
            string value;
            try { value = call() ?? ""; }
            catch (Exception) { value = ""; }
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length + 1 > cap) return -1;
            Marshal.Copy(bytes, 0, buf, bytes.Length);
            Marshal.WriteByte(buf, bytes.Length, 0);
            return bytes.Length;
        }

        private static string PtrToString(IntPtr utf8)
        {
            if (utf8 == IntPtr.Zero) return "";
            int len = 0;
            while (Marshal.ReadByte(utf8, len) != 0) len++;
            var bytes = new byte[len];
            Marshal.Copy(utf8, bytes, 0, len);
            return Encoding.UTF8.GetString(bytes);
        }

        // 原生侧用 \x01 拼参数；C# 按约定拆
        private static readonly NativeCallback AjaxCallback = delegate (IntPtr utf8In, IntPtr buf, int cap)
        {
            return WriteResult(delegate { return _host.Ajax(PtrToString(utf8In)) ?? ""; }, buf, cap);
        };

        private static readonly NativeCallback PostCallback = delegate (IntPtr utf8In, IntPtr buf, int cap)
        {
            var parts = PtrToString(utf8In).Split('\u0001');
            return WriteResult(delegate
            {
                return _host.Post(parts.Length > 0 ? parts[0] : "",
                    parts.Length > 1 ? parts[1] : "",
                    parts.Length > 2 ? parts[2] : "") ?? "";
            }, buf, cap);
        };

        private static readonly NativeCallback HttpGetCallback = delegate (IntPtr utf8In, IntPtr buf, int cap)
        {
            var parts = PtrToString(utf8In).Split('\u0001');
            return WriteResult(delegate
            {
                return _host.HttpGet(parts.Length > 0 ? parts[0] : "",
                    parts.Length > 1 ? parts[1] : "") ?? "";
            }, buf, cap);
        };

        private static readonly NativeCallback RuleCallback = delegate (IntPtr utf8In, IntPtr buf, int cap)
        {
            return WriteResult(delegate { return _host.RuleString(PtrToString(utf8In)) ?? ""; }, buf, cap);
        };

        private static readonly NativeCallback CookieCallback = delegate (IntPtr utf8In, IntPtr buf, int cap)
        {
            var parts = PtrToString(utf8In).Split('\u0001');
            return WriteResult(delegate
            {
                return _host.GetCookie(parts.Length > 0 ? parts[0] : "",
                    parts.Length > 1 ? parts[1] : "") ?? "";
            }, buf, cap);
        };

        private static readonly NativeCallback GetVarCallback = delegate (IntPtr utf8In, IntPtr buf, int cap)
        {
            return WriteResult(delegate { return _host.GetVar(PtrToString(utf8In)) ?? ""; }, buf, cap);
        };

        private static readonly NativeCallback PutCallback = delegate (IntPtr utf8In, IntPtr buf, int cap)
        {
            var parts = PtrToString(utf8In).Split('\u0001');
            return WriteResult(delegate
            {
                _host.Put(parts.Length > 0 ? parts[0] : "", parts.Length > 1 ? parts[1] : "");
                return "";
            }, buf, cap);
        };

        private static readonly NativeCallback Base64EncodeCallback = delegate (IntPtr utf8In, IntPtr buf, int cap)
        {
            return WriteResult(delegate { return Convert.ToBase64String(Encoding.UTF8.GetBytes(PtrToString(utf8In))); }, buf, cap);
        };

        private static readonly NativeCallback Base64DecodeCallback = delegate (IntPtr utf8In, IntPtr buf, int cap)
        {
            return WriteResult(delegate
            {
                try
                {
                    string text = PtrToString(utf8In).Trim().Replace(" ", "+").Replace("\n", "").Replace("\r", "");
                    return Encoding.UTF8.GetString(Convert.FromBase64String(text));
                }
                catch (Exception) { return ""; }
            }, buf, cap);
        };

        private static readonly NativeCallback Base64UrlCallback = delegate (IntPtr utf8In, IntPtr buf, int cap)
        {
            return WriteResult(delegate
            {
                return Convert.ToBase64String(Encoding.UTF8.GetBytes(PtrToString(utf8In)))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            }, buf, cap);
        };

        private static readonly NativeCallback HashCallback = delegate (IntPtr utf8In, IntPtr buf, int cap)
        {
            // 原生侧把算法名放在第一个参数：md5 / md5_16 / sha1 / sha256
            var parts = PtrToString(utf8In).Split('\u0001');
            return WriteResult(delegate
            {
                string text = parts.Length > 1 ? parts[1] : "";
                string algorithm = parts.Length > 0 ? parts[0] : "md5";
                using (System.Security.Cryptography.HashAlgorithm hash =
                    algorithm == "sha1" ? (System.Security.Cryptography.HashAlgorithm)System.Security.Cryptography.SHA1.Create() :
                    algorithm == "sha256" ? (System.Security.Cryptography.HashAlgorithm)System.Security.Cryptography.SHA256.Create() :
                    (System.Security.Cryptography.HashAlgorithm)System.Security.Cryptography.MD5.Create())
                {
                    byte[] bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(text));
                    var sb = new StringBuilder(bytes.Length * 2);
                    foreach (byte b in bytes) sb.Append(b.ToString("x2"));
                    string hex = sb.ToString();
                    return algorithm == "md5_16" ? hex.Substring(8, 16) : hex;
                }
            }, buf, cap);
        };

        private static readonly NativeCallback AesDecodeCallback = delegate (IntPtr utf8In, IntPtr buf, int cap)
        {
            var parts = PtrToString(utf8In).Split('\u0001');
            return WriteResult(delegate
            {
                return AesTransform(parts.Length > 1 ? parts[1] : "", parts.Length > 2 ? parts[2] : "",
                    parts.Length > 3 ? parts[3] : "", parts.Length > 4 ? parts[4] : "", false);
            }, buf, cap);
        };

        private static readonly NativeCallback AesEncodeCallback = delegate (IntPtr utf8In, IntPtr buf, int cap)
        {
            var parts = PtrToString(utf8In).Split('\u0001');
            return WriteResult(delegate
            {
                return AesTransform(parts.Length > 1 ? parts[1] : "", parts.Length > 2 ? parts[2] : "",
                    parts.Length > 3 ? parts[3] : "", parts.Length > 4 ? parts[4] : "", true);
            }, buf, cap);
        };

        private static string AesTransform(string data, string key, string transformation, string iv, bool encrypt)
        {
            string upper = (transformation ?? "").ToUpperInvariant();
            bool cbc = upper.Contains("CBC");
            bool noPadding = upper.Contains("NOPADDING");

            byte[] keyBytes = Encoding.UTF8.GetBytes(key ?? "");
            Array.Resize(ref keyBytes, 16);
            byte[] input = encrypt
                ? Encoding.UTF8.GetBytes(data ?? "")
                : Convert.FromBase64String((data ?? "").Trim().Replace(" ", "+"));

            using (var aes = System.Security.Cryptography.Aes.Create())
            {
                aes.Key = keyBytes;
                aes.Mode = cbc ? System.Security.Cryptography.CipherMode.CBC : System.Security.Cryptography.CipherMode.ECB;
                aes.Padding = noPadding ? System.Security.Cryptography.PaddingMode.None : System.Security.Cryptography.PaddingMode.PKCS7;
                if (cbc)
                {
                    byte[] ivBytes = Encoding.UTF8.GetBytes(iv ?? "");
                    Array.Resize(ref ivBytes, 16);
                    aes.IV = ivBytes;
                }
                var transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
                byte[] output = transform.TransformFinalBlock(input, 0, input.Length);
                return encrypt ? Convert.ToBase64String(output) : Encoding.UTF8.GetString(output);
            }
        }

        // ------------------------------------------------------------ P/Invoke

        // 不对 JsBridge 使用 DllImport：.NET Native 会将它写入启动导入表，
        // 缺少任一原生依赖时应用在进入 C# 前就被终止，catch 无法提供回退。
        [DllImport("api-ms-win-core-libraryloader-l2-1-0.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr LoadPackagedLibrary(string name, uint reserved);

        [DllImport("api-ms-win-core-libraryloader-l1-2-0.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InitFunction(out IntPtr context, IntPtr diagnosticPath,
            IntPtr ajax, IntPtr post, IntPtr httpGet, IntPtr rule, IntPtr cookie,
            IntPtr getVar, IntPtr put, IntPtr base64e, IntPtr base64d, IntPtr base64url,
            IntPtr md5, IntPtr md5_16, IntPtr sha1, IntPtr sha256,
            IntPtr aesDecode, IntPtr aesEncode);

        /// <summary>绑定串：名\u0003值（字符串）/ 名\u0004JSON，条目间 \u0002。ok=0 成功。</summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr EvaluateFunction(IntPtr context, IntPtr scriptUni, IntPtr bindingsUni, out int ok);

        private static IntPtr FindExport(string name)
        {
            IntPtr address = GetProcAddress(_module, name);
            if (address == IntPtr.Zero) throw new InvalidOperationException("JsBridge 缺少导出：" + name);
            return address;
        }

        private static IntPtr CallbackPointer(NativeCallback callback)
        {
            // 所有回调由 static readonly 字段持有，原生上下文存在期间不能被 GC 回收。
            return Marshal.GetFunctionPointerForDelegate(callback);
        }

        public string LastError { get; private set; }

        /// <summary>JsBridge.dll 能加载并完成初始化时可用（只在 ARM 包里有这个 DLL）。</summary>
        public static bool IsAvailable
        {
            get
            {
                lock (_gate)
                {
                    if (_available.HasValue) return _available.Value;
                    IntPtr diagnosticPath = IntPtr.Zero;
                    try
                    {
                        TraceStage("loading JsBridge.dll");
                        _module = LoadPackagedLibrary(Dll, 0);
                        if (_module == IntPtr.Zero)
                            throw new InvalidOperationException("LoadPackagedLibrary(JsBridge.dll) 失败，Win32=" + Marshal.GetLastWin32Error());
                        _init = Marshal.GetDelegateForFunctionPointer<InitFunction>(FindExport("JsBridgeInit"));
                        _evaluate = Marshal.GetDelegateForFunctionPointer<EvaluateFunction>(FindExport("JsBridgeEvaluate"));
                        diagnosticPath = Marshal.StringToHGlobalUni(DiagnosticPathProvider == null ? "" : DiagnosticPathProvider());
                        TraceStage("calling JsBridgeInit");
                        IntPtr ctx;
                        int rc = _init(out ctx, diagnosticPath,
                            CallbackPointer(AjaxCallback), CallbackPointer(PostCallback), CallbackPointer(HttpGetCallback), CallbackPointer(RuleCallback), CallbackPointer(CookieCallback),
                            CallbackPointer(GetVarCallback), CallbackPointer(PutCallback), CallbackPointer(Base64EncodeCallback), CallbackPointer(Base64DecodeCallback), CallbackPointer(Base64UrlCallback),
                            CallbackPointer(HashCallback), CallbackPointer(HashCallback), CallbackPointer(HashCallback), CallbackPointer(HashCallback),
                            CallbackPointer(AesDecodeCallback), CallbackPointer(AesEncodeCallback));
                        _available = rc == 0 && ctx != IntPtr.Zero;
                        TraceStage("JsBridgeInit returned " + rc);
                        if (_available.Value) _ctx = ctx;
                        else _lastError = "JsBridgeInit 返回 " + rc;
                    }
                    catch (Exception ex)
                    {
                        _available = false;
                        _lastError = ex.Message;
                    }
                    finally { Marshal.FreeHGlobal(diagnosticPath); }
                    if (!_available.Value)
                        NovReader.Services.AppState.Log("JS 引擎不可用：" + _lastError);
                    return _available.Value;
                }
            }
        }

        public string Evaluate(string script, IDictionary<string, string> bindings,
                               IDictionary<string, string> jsonBindings, JsHost host)
        {
            LastError = "";
            if (!IsAvailable) { LastError = _lastError; return null; }

            var sb = new StringBuilder(1024);
            AppendBindings(sb, bindings, '\u0003');
            AppendBindings(sb, jsonBindings, '\u0004');
            // 原生上下文、返回缓冲区和宿主均为进程级共享；读取结果也必须在同一把锁内。
            // 仅靠每个 LegadoJs 实例的锁无法阻止多个书源互相覆盖 _host 和结果。
            lock (_gate)
            {
                JsHost previousHost = _host;
                _host = host;
                IntPtr scriptPtr = IntPtr.Zero;
                IntPtr bindPtr = IntPtr.Zero;
                try
                {
                    scriptPtr = Marshal.StringToHGlobalUni(script);
                    bindPtr = Marshal.StringToHGlobalUni(sb.ToString());
                    int ok;
                    TraceStage("calling JsBridgeEvaluate; script chars=" + (script == null ? 0 : script.Length));
                    IntPtr result = _evaluate(_ctx, scriptPtr, bindPtr, out ok);
                    TraceStage("JsBridgeEvaluate returned " + ok);
                    if (ok != 0)
                    {
                        LastError = PtrToString(result) ?? "JS 求值失败";
                        TraceStage("JS error: " + LastError);
                        return null;
                    }
                    return PtrToString(result) ?? "";
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    return null;
                }
                finally
                {
                    Marshal.FreeHGlobal(scriptPtr);
                    Marshal.FreeHGlobal(bindPtr);
                    _host = previousHost;
                }
            }
        }

        private static void AppendBindings(StringBuilder sb, IDictionary<string, string> map, char sep)
        {
            foreach (var pair in map)
            {
                if (sb.Length > 0) sb.Append('\u0002');
                sb.Append(pair.Key).Append(sep).Append(pair.Value ?? "");
            }
        }

        public void Dispose()
        {
            // 进程级单例，不做逐次释放
        }
    }
}
