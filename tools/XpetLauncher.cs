using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Xpet")]
[assembly: System.Reflection.AssemblyProduct("Xpet")]
[assembly: System.Reflection.AssemblyVersion("0.6.0.0")]

/// <summary>Windowless entry point. Owns only processes it starts; reused backends remain independent.</summary>
internal static class XpetLauncher
{
    private static readonly object LogLock = new object();
    private static string LogDirectory;
    private static string LogFile;
    private static Process OwnedBackend;
    private static string Token;
    private static string BaseUrl;
    private static string HealthFailure;
    private static Job ProcessJob;
    private static bool BackendAssigned;
    private static bool SilentErrors;

    [STAThread]
    private static int Main(string[] args)
    {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        int port = 18080;
        string isolatedData = null;
        bool explicitRoot = false;
        int verifyExitAfter = 0;
        try
        {
            for (int index = 0; index < args.Length; index++)
            {
                if (args[index] == "--silent-errors") SilentErrors = true;
                else if (args[index] == "--root" && index + 1 < args.Length) { root = Path.GetFullPath(args[++index]); explicitRoot = true; }
                else if (args[index] == "--port" && index + 1 < args.Length) port = int.Parse(args[++index]);
                else if (args[index] == "--data-dir" && index + 1 < args.Length) isolatedData = Path.GetFullPath(args[++index]);
                else if (args[index] == "--verify-exit-after" && index + 1 < args.Length) verifyExitAfter = int.Parse(args[++index]);
                else throw new ArgumentException("Unknown launcher argument.");
            }
            if (port < 1024 || port > 65535) throw new ArgumentException("Invalid local port.");
            if (verifyExitAfter != 0 && (verifyExitAfter < 5 || verifyExitAfter > 90 || isolatedData == null || port == 18080 || port == 18081))
                throw new ArgumentException("Release verification requires 5–90 seconds, isolated data and a separate local port.");
            if (!explicitRoot && !File.Exists(Path.Combine(root, "dist", "Xpet", "Xpet.Client.exe")))
            {
                string package = Path.Combine(root, "dist", "Xpet-" + typeof(XpetLauncher).Assembly.GetName().Version.ToString(3));
                if (File.Exists(Path.Combine(package, "dist", "Xpet", "Xpet.Client.exe"))
                    && File.Exists(Path.Combine(package, "pet-backend", "XpetBackend.exe"))) root = package;
            }
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            LogDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Xpet", "logs");
            if (isolatedData != null) LogDirectory = Path.Combine(isolatedData, "logs");
            Directory.CreateDirectory(LogDirectory);
            LogFile = Path.Combine(LogDirectory, "launcher.log");
            if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 2 * 1024 * 1024)
            {
                string previous = Path.Combine(LogDirectory, "launcher.previous.log");
                if (File.Exists(previous)) File.Delete(previous);
                File.Move(LogFile, previous);
            }
            BaseUrl = "http://127.0.0.1:" + port;
            string instanceName = "Local\\Xpet." + Hash(root.ToLowerInvariant() + ":" + port).Substring(0, 24);
            bool created;
            using (Mutex mutex = new Mutex(true, instanceName, out created))
            {
                if (!created) { Log("Already running; repeated launch ignored."); return 0; }
                try { return Run(root, isolatedData, verifyExitAfter); }
                finally
                {
                    StopOwnedBackend();
                    if (ProcessJob != null) { ProcessJob.Dispose(); ProcessJob = null; }
                    if (OwnedBackend != null) { OwnedBackend.Dispose(); OwnedBackend = null; }
                    mutex.ReleaseMutex();
                }
            }
        }
        catch (Exception error)
        {
            Log("Launch failed: " + error.GetType().Name + ": " + error.Message);
            if (!SilentErrors) MessageBox.Show("Xpet 启动失败：" + error.Message + "\n\n日志：" + (LogFile ?? "不可用"), "Xpet", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            StopOwnedBackend();
            if (ProcessJob != null) ProcessJob.Dispose();
            if (OwnedBackend != null) OwnedBackend.Dispose();
        }
    }

    private static int Run(string root, string isolatedData, int verifyExitAfter)
    {
        string client = Path.Combine(root, "dist", "Xpet", "Xpet.Client.exe");
        string backend = Path.Combine(root, "pet-backend", "XpetBackend.exe");
        if (!File.Exists(client)) throw new FileNotFoundException("客户端文件缺失，请完整解压 Xpet 发行包。", client);
        string expectedBackendApp = isolatedData == null ? "desktop-pet-backend" : "desktop-pet-backend-qa";
        bool existing = Healthy(expectedBackendApp);
        if (!existing && PortOpen()) throw new InvalidOperationException(HealthFailure ?? "本地端口已有其他服务，Xpet 未修改或关闭该服务。");
        ProcessJob = new Job();
        if (!existing)
        {
            if (!File.Exists(backend)) throw new FileNotFoundException("后端文件缺失，请完整解压 Xpet 发行包。", backend);
            byte[] random = new byte[32]; using (RandomNumberGenerator generator = RandomNumberGenerator.Create()) generator.GetBytes(random);
            Token = Hex(random);
            string parameters = "--port " + BaseUrl.Substring(BaseUrl.LastIndexOf(':') + 1) + " --shutdown-token " + Token;
            if (isolatedData != null) parameters += " --data-dir " + Quote(isolatedData);
            OwnedBackend = Start(backend, parameters, true);
            ProcessJob.Assign(OwnedBackend);
            BackendAssigned = true;
            Log("Started owned backend PID " + OwnedBackend.Id + ".");
            DateTime deadline = DateTime.UtcNow.AddSeconds(45);
            bool ready = false;
            while (DateTime.UtcNow < deadline)
            {
                if (OwnedBackend.HasExited) throw new InvalidOperationException("后端启动失败，请查看 backend.log。");
                if (Healthy(expectedBackendApp) && OwnedStatus()) { ready = true; break; }
                Thread.Sleep(180);
            }
            if (!ready) throw new TimeoutException("等待后端就绪超时，请查看 backend.log。");
        }
        else Log("Reusing pre-existing healthy backend; launcher will preserve it.");
        if (isolatedData == null) MigrateLegacyPreferences();
        string clientArguments = isolatedData != null || BaseUrl != "http://127.0.0.1:18080" ? "--backend-base-url " + Quote(BaseUrl) : "";
        clientArguments += " --xpet-library-root " + Quote(isolatedData ?? root);
        if (isolatedData != null) clientArguments += " --xpet-isolated-libraries";
        if (verifyExitAfter > 0) clientArguments += " --xpet-verify-exit-after " + verifyExitAfter;
        using (Process process = Start(client, clientArguments, false))
        {
            try { ProcessJob.Assign(process); }
            catch { if (!process.HasExited) process.Kill(); throw; }
            Log("Started client PID " + process.Id + ".");
            process.WaitForExit();
            Log("Client exited with code " + process.ExitCode + ".");
            if (process.ExitCode != 0) throw new InvalidOperationException("客户端异常退出，退出码 " + process.ExitCode + "；请查看 Player.log。");
        }
        return 0;
    }

    private static void MigrateLegacyPreferences()
    {
        try
        {
            using (RegistryKey previous = Registry.CurrentUser.OpenSubKey("Software\\DesktopPet\\Lita Desktop Pet"))
            using (RegistryKey current = Registry.CurrentUser.CreateSubKey("Software\\DesktopPet\\Xpet"))
            {
                bool hasPreferences = false;
                foreach (string name in current.GetValueNames()) if (name.StartsWith("desktop_pet.", StringComparison.Ordinal)) { hasPreferences = true; break; }
                if (!hasPreferences && previous != null)
                    foreach (string name in previous.GetValueNames())
                        if (name.StartsWith("desktop_pet.", StringComparison.Ordinal) && current.GetValue(name) == null)
                            current.SetValue(name, previous.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), previous.GetValueKind(name));
            }
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string localLow = Path.Combine(Path.GetDirectoryName(local), "LocalLow", "DesktopPet");
            string source = Path.Combine(localLow, "Lita Desktop Pet", "pet-motions");
            string target = Path.Combine(localLow, "Xpet", "pet-motions");
            if (Directory.Exists(source) && !Directory.Exists(target))
            {
                Directory.CreateDirectory(target);
                foreach (string motion in Directory.GetFiles(source, "*.vmd", SearchOption.TopDirectoryOnly))
                    File.Copy(motion, Path.Combine(target, Path.GetFileName(motion)), false);
            }
            Log("Legacy Xpet preferences migration checked.");
        }
        catch (Exception error) { Log("Legacy preferences migration: " + error.GetType().Name + ": " + error.Message); }
    }

    private static Process Start(string executable, string args, bool backend)
    {
        var info = new ProcessStartInfo(executable, args);
        info.WorkingDirectory = Path.GetDirectoryName(executable);
        info.UseShellExecute = false; info.CreateNoWindow = true; info.WindowStyle = backend ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal;
        info.ErrorDialog = false;
        if (backend) info.EnvironmentVariables["XPET_LOG_DIR"] = LogDirectory;
        Process process = Process.Start(info);
        if (process == null) throw new InvalidOperationException("无法创建 Xpet 进程。");
        return process;
    }

    private static bool Healthy(string expectedBackendApp)
    {
        HealthFailure = null;
        try
        {
            var data = Request("/health", "GET", null);
            bool app = data != null && data.ContainsKey("app") && Convert.ToString(data["app"]) == expectedBackendApp
                && data.ContainsKey("ok") && data["ok"] is bool && (bool)data["ok"];
            if (!app) return false;
            Version version;
            if (!data.ContainsKey("version") || !Version.TryParse(Convert.ToString(data["version"]), out version) || version < new Version(0, 6, 0))
            {
                HealthFailure = "已运行的桌宠后端版本过旧或未知，请关闭旧服务后重试。Xpet 保留了该服务。";
                return false;
            }
            return true;
        }
        catch { return false; }
    }
    private static bool OwnedStatus()
    {
        try
        {
            var data = Request("/internal/launcher/status", "GET", Token);
            if (data == null || !data.ContainsKey("pid")) return false;
            int serverPid = Convert.ToInt32(data["pid"]);
            return serverPid == OwnedBackend.Id || (ProcessJob != null && BackendAssigned && ProcessJob.Contains(serverPid));
        }
        catch { return false; }
    }
    private static Dictionary<string, object> Request(string path, string method, string token)
    {
        var request = (HttpWebRequest)WebRequest.Create(BaseUrl + path);
        request.Proxy = null; request.Method = method; request.Timeout = 1500; request.ReadWriteTimeout = 1500;
        if (token != null) request.Headers["X-Xpet-Shutdown-Token"] = token;
        if (method == "POST") request.ContentLength = 0;
        using (var response = (HttpWebResponse)request.GetResponse()) using (var stream = response.GetResponseStream()) using (var reader = new StreamReader(stream))
            return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
    }
    private static bool PortOpen()
    {
        try
        {
            using (var client = new System.Net.Sockets.TcpClient())
            {
                IAsyncResult pending = client.BeginConnect("127.0.0.1", int.Parse(BaseUrl.Substring(BaseUrl.LastIndexOf(':') + 1)), null, null);
                if (!pending.AsyncWaitHandle.WaitOne(1000)) return false;
                client.EndConnect(pending); return true;
            }
        }
        catch { return false; }
    }

    private static void StopOwnedBackend()
    {
        if (OwnedBackend == null) return;
        try
        {
            if (OwnedBackend.HasExited) return;
            // A PID match plus the nonce prevents shutting down a raced or replaced service.
            try { if (OwnedStatus()) { Request("/internal/launcher/shutdown", "POST", Token); Log("Requested graceful backend shutdown."); } }
            catch (Exception error) { Log("Graceful shutdown request failed: " + error.GetType().Name + "."); }
            if (OwnedBackend.WaitForExit(10000)) { Log("Owned backend exited gracefully."); return; }
            Log("Owned backend exceeded shutdown timeout; closing only the owned process job.");
            if (ProcessJob != null && BackendAssigned) ProcessJob.Terminate();
            else KillOwnedTree();
            OwnedBackend.WaitForExit(3000);
        }
        catch (Exception error) { Log("Backend cleanup: " + error.GetType().Name + ": " + error.Message); }
    }

    private static void KillOwnedTree()
    {
        if (OwnedBackend == null || OwnedBackend.HasExited) return;
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe"), "/PID " + OwnedBackend.Id + " /T /F");
        info.UseShellExecute = false; info.CreateNoWindow = true; info.WindowStyle = ProcessWindowStyle.Hidden;
        info.RedirectStandardOutput = true; info.RedirectStandardError = true;
        using (Process stop = Process.Start(info)) { stop.WaitForExit(3000); }
        if (!OwnedBackend.HasExited) OwnedBackend.Kill();
    }

    private static void Log(string text)
    {
        if (LogFile == null) return;
        try { lock (LogLock) File.AppendAllText(LogFile, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") + " " + text + Environment.NewLine, Encoding.UTF8); } catch { }
    }
    private static string Hash(string value) { using (SHA256 sha = SHA256.Create()) return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(value))); }
    private static string Hex(byte[] value) { var text = new StringBuilder(); foreach (byte item in value) text.Append(item.ToString("x2")); return text.ToString(); }
    private static string Quote(string value)
    {
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char item in value)
        {
            if (item == '\\') { slashes++; continue; }
            if (item == '"') { result.Append('\\', slashes * 2 + 1); result.Append(item); slashes = 0; continue; }
            result.Append('\\', slashes); slashes = 0; result.Append(item);
        }
        result.Append('\\', slashes * 2); result.Append('"'); return result.ToString();
    }

    private sealed class Job : IDisposable
    {
        private IntPtr handle;
        public Job()
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var limits = new ExtendedLimits(); limits.BasicLimitInformation.LimitFlags = 0x00002000; // KILL_ON_JOB_CLOSE
            int size = Marshal.SizeOf(typeof(ExtendedLimits)); IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, buffer, false);
                if (!SetInformationJobObject(handle, 9, buffer, (uint)size)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        public void Assign(Process process)
        {
            if (!AssignProcessToJobObject(handle, process.Handle)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        public bool Contains(int pid)
        {
            IntPtr process = OpenProcess(0x1000, false, pid);
            if (process == IntPtr.Zero) return false;
            try { bool belongs; return IsProcessInJob(process, handle, out belongs) && belongs; }
            finally { CloseHandle(process); }
        }
        public void Terminate() { if (handle != IntPtr.Zero) TerminateJobObject(handle, 1); }
        public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
        {
            public BasicLimits BasicLimitInformation; public IoCounters IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    }
}
