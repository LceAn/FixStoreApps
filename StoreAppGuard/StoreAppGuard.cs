using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

// ============================================================
//  StoreAppGuard - 商店应用守护进程
//  常驻托盘, 实时监听 AppX 激活失败事件, 自动按修复阶梯处理:
//    第1级 重注册 -> 第2级 重建数据目录+重注册 -> 第3级 Reset
//    三级都失败 -> 通知人工处理(重装), 24小时内不再自动尝试
//  修复动作复用 PowerShell (Add-AppxPackage / Reset-AppxPackage),
//  经 -EncodedCommand 调用, 全部为当前用户级操作, 无需管理员权限。
//  编译: build.bat (系统自带 csc, C# 5 语法)
// ============================================================

namespace StoreAppGuard
{
    internal static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint dwProcessId);
        private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

        [STAThread]
        internal static void Main(string[] args)
        {
            // ---- 命令行模式: StoreAppGuard.exe --once [--days N] ----
            bool once = false; int days = 3;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--once" || args[i] == "/once") once = true;
                if ((args[i] == "--days" || args[i] == "/days") && i + 1 < args.Length)
                {
                    int d; if (int.TryParse(args[i + 1], out d) && d > 0 && d <= 365) days = d;
                }
            }
            if (once)
            {
                try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { }
                Engine e = new Engine(null);
                e.RunSweepOnce(days);
                return;
            }

            // ---- 常驻模式: 单实例 + 托盘 ----
            bool createdNew;
            Mutex mtx = new Mutex(true, "StoreAppGuard_SingleInstance", out createdNew);
            if (!createdNew) return; // 已在运行
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new GuardContext());
            GC.KeepAlive(mtx);
        }
    }

    // ==================== 托盘上下文 ====================
    internal sealed class GuardContext : ApplicationContext
    {
        private readonly NotifyIcon tray;
        private readonly Control syncCtrl = new Control();   // 跨线程通知的同步对象
        private readonly Engine engine;
        private readonly Icon icoGreen, icoOrange;
        private readonly ToolStripMenuItem miAutoStart;

        internal GuardContext()
        {
            IntPtr h = syncCtrl.Handle; // 强制创建句柄, 供 BeginInvoke
            icoGreen = MakeIcon(Color.FromArgb(46, 160, 67), true);
            icoOrange = MakeIcon(Color.FromArgb(230, 140, 30), false);

            engine = new Engine(UiNotify);
            engine.BusyChanged += Engine_BusyChanged;

            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("立即检查修复", null, delegate { RunBg(delegate { engine.RunSweepOnce(3); }, "手动扫描"); }));
            menu.Items.Add(new ToolStripMenuItem("打开日志目录", null, delegate { TryOpenDir(engine.LogDir); }));
            miAutoStart = new ToolStripMenuItem("开机自动启动", null, MenuAutoStartToggle);
            miAutoStart.Checked = AutoStartEnabled;
            menu.Items.Add(miAutoStart);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("关于 StoreAppGuard", null, delegate {
                MessageBox.Show("商店应用守护进程 StoreAppGuard v1.0\r\n\r\n"
                    + "实时监听商店应用激活失败事件, 自动执行修复阶梯:\r\n"
                    + "  1. 重注册  2. 重建数据目录+重注册  3. 重置应用\r\n"
                    + "三级无效时通知人工重装 (24 小时内不再自动尝试)。\r\n\r\n"
                    + "源码: https://github.com/LceAn/FixStoreApps",
                    "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }));
            menu.Items.Add(new ToolStripMenuItem("退出", null, delegate {
                engine.Stop();
                tray.Visible = false;
                ExitThread();
            }));

            tray = new NotifyIcon();
            tray.Icon = icoGreen;
            tray.Text = "商店应用守护 - 运行中";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.DoubleClick += delegate { RunBg(delegate { engine.RunSweepOnce(3); }, "手动扫描"); };

            engine.Start();
            tray.ShowBalloonTip(4000, "商店应用守护已启动",
                "正在后台监听商店应用故障, 发现问题会自动修复并提示。", ToolTipIcon.Info);
        }

        private void Engine_BusyChanged(bool busy)
        {
            if (syncCtrl.IsHandleCreated)
            {
                try
                {
                    syncCtrl.BeginInvoke((MethodInvoker)delegate
                    {
                        tray.Icon = busy ? icoOrange : icoGreen;
                        tray.Text = busy ? "商店应用守护 - 正在修复..." : "商店应用守护 - 运行中";
                    });
                }
                catch { }
            }
        }

        // 引擎回调 -> 切回 UI 线程弹气泡
        private void UiNotify(string title, string msg, string kind)
        {
            if (!syncCtrl.IsHandleCreated) return;
            ToolTipIcon ico = ToolTipIcon.Info;
            if (kind == "warn") ico = ToolTipIcon.Warning;
            else if (kind == "error") ico = ToolTipIcon.Error;
            try
            {
                syncCtrl.BeginInvoke((MethodInvoker)delegate { tray.ShowBalloonTip(5000, title, msg, ico); });
            }
            catch { }
        }

        private void RunBg(Action a, string what)
        {
            Task.Factory.StartNew(delegate
            {
                try { a(); }
                catch (Exception ex) { engine.WriteLog("[x] " + what + " 异常: " + ex.Message); }
            });
        }

        private static void TryOpenDir(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", dir);
            }
            catch { }
        }

        // ---- 开机自启 (HKCU Run, 无需管理员) ----
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunName = "StoreAppGuard";
        private static bool AutoStartEnabled
        {
            get
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                    {
                        if (k == null) return false;
                        object v = k.GetValue(RunName);
                        return v != null && File.Exists(v.ToString());
                    }
                }
                catch { return false; }
            }
        }

        private void MenuAutoStartToggle(object s, EventArgs e)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (miAutoStart.Checked) k.DeleteValue(RunName, false);
                    else k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                }
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "设置失败"); }
            miAutoStart.Checked = AutoStartEnabled;
        }

        // ---- 运行时绘制托盘图标 (绿色对勾=正常, 橙色=修复中) ----
        private static Icon MakeIcon(Color back, bool check)
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (SolidBrush b = new SolidBrush(back)) g.FillEllipse(b, 2, 2, 28, 28);
                using (Pen p = new Pen(Color.White, 3.5f))
                {
                    p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                    if (check)
                    {
                        g.DrawLine(p, 9, 17, 14, 22);
                        g.DrawLine(p, 14, 22, 24, 10);
                    }
                    else
                    {
                        g.DrawLine(p, 16, 9, 16, 18);
                        g.FillEllipse(Brushes.White, 13, 21, 6, 6);
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }

    // ==================== 守护引擎 ====================
    internal sealed class Engine
    {
        // 事件来源
        private const string LogAppModel = "Microsoft-Windows-AppModel-Runtime/Admin";
        private const string LogTWinUI = "Microsoft-Windows-TWinUI/Operational";

        private static readonly Regex RxMsgFull = new Regex(@"([A-Za-z0-9.\-]+_\d+(?:\.\d+)*_(?:x64|arm64|x86|neutral)__[A-Za-z0-9]+)");
        private static readonly Regex RxMsgFam = new Regex(@"([A-Za-z0-9.]+_[A-Za-z0-9]{13})");
        private static readonly Regex RxFull2Fam = new Regex(@"^([A-Za-z0-9.\-]+)_\d+(?:\.\d+)*_(?:x64|arm64|x86|neutral)__([A-Za-z0-9]+)$");

        // 节流参数
        private const int DebounceMs = 45000;   // 事件触发后等待窗口
        private const int CooldownMin = 15;     // 同一应用两次修复的最小间隔
        private const int ManualHours = 24;     // 三级无效后的人工处理冷静期

        private readonly Action<string, string, string> notify; // 可为 null (--once)
        private readonly object gate = new object();
        private readonly Dictionary<string, PkgState> states = new Dictionary<string, PkgState>();
        private readonly Dictionary<string, DateTime> pending = new Dictionary<string, DateTime>();
        private readonly List<EventLog> evLogs = new List<EventLog>();
        private System.Threading.Timer pollTimer, debounceTimer, startTimer;
        private bool busy;

        internal string BaseDir { get; private set; }
        internal string LogDir { get; private set; }
        private string StatePath { get; set; }
        private string LogFile
        {
            get { return Path.Combine(LogDir, "guard-" + DateTime.Now.ToString("yyyyMMdd") + ".log"); }
        }

        internal event Action<bool> BusyChanged;

        private sealed class PkgState
        {
            internal int Rung;
            internal DateTime LastRepair = DateTime.MinValue;
            internal DateTime LastFailure = DateTime.MinValue;
            internal DateTime ManualUntil = DateTime.MinValue;
            internal bool NotifiedManual;
        }

        internal Engine(Action<string, string, string> notifyCb)
        {
            notify = notifyCb;
            BaseDir = AppDomain.CurrentDomain.BaseDirectory;
            LogDir = Path.Combine(BaseDir, "logs");
            StatePath = Path.Combine(BaseDir, "guard-state.txt");
            try { if (!Directory.Exists(LogDir)) Directory.CreateDirectory(LogDir); } catch { }
        }

        // ---------------- 生命周期 ----------------

        internal void Start()
        {
            LoadState();
            Subscribe(LogAppModel);
            Subscribe(LogTWinUI);
            debounceTimer = new System.Threading.Timer(delegate { ProcessPending(); }, null, Timeout.Infinite, Timeout.Infinite);
            // 启动 20 秒后补一次启动期扫描(近1天), 之后每 5 分钟轮询兜底(近6分钟)
            startTimer = new System.Threading.Timer(delegate { RunSweepMinutes(60 * 24); startTimer.Dispose(); }, null, 20000, Timeout.Infinite);
            pollTimer = new System.Threading.Timer(delegate { RunSweepMinutes(6); }, null, 90000, 300000);
            WriteLog("[ok] 守护启动, 事件监听 + 周期巡检已就绪");
        }

        internal void Stop()
        {
            lock (gate)
            {
                if (pollTimer != null) pollTimer.Dispose();
                if (debounceTimer != null) debounceTimer.Dispose();
                if (startTimer != null) startTimer.Dispose();
                foreach (EventLog l in evLogs)
                {
                    try { l.EnableRaisingEvents = false; l.Dispose(); } catch { }
                }
                evLogs.Clear();
            }
            WriteLog("守护停止");
        }

        private void Subscribe(string logName)
        {
            try
            {
                if (!EventLog.Exists(logName)) return;
                EventLog l = new EventLog(logName);
                l.EntryWritten += delegate(object s, EntryWrittenEventArgs e) { OnEvent(logName, e.Entry); };
                l.EnableRaisingEvents = true;
                evLogs.Add(l);
            }
            catch (Exception ex) { WriteLog("[!] 无法订阅 " + logName + ": " + ex.Message); }
        }

        // ---------------- 事件入口 ----------------

        private void OnEvent(string logName, EventLogEntry en)
        {
            try
            {
                if (logName == LogAppModel)
                {
                    if (en.EntryType != EventLogEntryType.Error) return;
                    if (en.InstanceId != 208 && en.InstanceId != 216) return;
                }
                else // TWinUI 5973 = 激活失败(应用无法启动)
                {
                    if (en.InstanceId != 5973) return;
                }
                string msg = en.Message ?? "";
                string fam = ExtractFamily(msg);
                if (fam == null) return;
                lock (gate) { pending[fam] = DateTime.Now; }
                WriteLog("事件: " + fam + " 激活失败 (" + logName.Split('/')[0] + " " + en.InstanceId + "), 45 秒防抖后处理");
                ResetDebounce();
            }
            catch { }
        }

        private static string ExtractFamily(string msg)
        {
            Match m = RxMsgFull.Match(msg);
            if (m.Success) return FullToFam(m.Groups[1].Value);
            m = RxMsgFam.Match(msg);
            if (m.Success) return m.Groups[1].Value;
            return null;
        }

        private static string FullToFam(string full)
        {
            Match m = RxFull2Fam.Match(full);
            return m.Success ? m.Groups[1].Value + "_" + m.Groups[2].Value : full;
        }

        private void ResetDebounce()
        {
            if (debounceTimer == null) return;
            debounceTimer.Change(DebounceMs, Timeout.Infinite);
        }

        // ---------------- 处理队列 ----------------

        private void ProcessPending()
        {
            List<KeyValuePair<string, DateTime>> work;
            lock (gate)
            {
                work = pending.ToList();
                pending.Clear();
            }
            if (work.Count == 0) return;
            foreach (KeyValuePair<string, DateTime> kv in work) HandleFamily(kv.Key);
        }

        // 扫描最近 N 分钟的失败记录并处理 (巡检/手动/启动期共用)
        internal void RunSweepMinutes(int minutes)
        {
            Dictionary<string, int> fams = SweepFamilies(minutes);
            if (fams == null || fams.Count == 0)
            {
                WriteLog("巡检: 最近 " + minutes + " 分钟无激活失败记录");
                return;
            }
            WriteLog("巡检: 发现 " + fams.Count + " 个有失败记录的应用: " + string.Join(", ", fams.Keys.ToArray()));
            foreach (string fam in fams.Keys) HandleFamily(fam);
        }

        // 扫描最近 N 天 (命令行 --once / 菜单手动)
        internal void RunSweepOnce(int days)
        {
            WriteLog("==== 一次性扫描: 最近 " + days + " 天 ====");
            Dictionary<string, int> fams = SweepFamilies(days * 24 * 60);
            if (fams == null || fams.Count == 0)
            {
                WriteLog("[ok] 无激活失败记录, 全部健康");
                Console.WriteLine("CLEAN: no activation failures in last " + days + " days");
                return;
            }
            foreach (KeyValuePair<string, int> kv in fams)
                WriteLog("  " + kv.Key + "  失败 " + kv.Value + " 次");
            foreach (string fam in fams.Keys) HandleFamily(fam);
            Console.WriteLine("DONE, see log: " + LogFile);
        }

        private void HandleFamily(string fam)
        {
            try
            {
                DateTime now = DateTime.Now;
                lock (gate)
                {
                    PkgState st;
                    states.TryGetValue(fam, out st);
                    if (st != null)
                    {
                        st.LastFailure = now;
                        if (st.ManualUntil > now)
                        {
                            if (!st.NotifiedManual)
                            {
                                st.NotifiedManual = true; SaveState();
                                DoNotify("需人工处理: " + fam, "自动修复多轮无效, 已暂停自动尝试至 " + st.ManualUntil.ToString("MM-dd HH:mm") + ", 建议用『一键修复商店应用』重装。", "warn");
                            }
                            else WriteLog("跳过 " + fam + " : 处于人工处理冷静期");
                            return;
                        }
                        if (now - st.LastRepair < TimeSpan.FromMinutes(CooldownMin))
                        {
                            WriteLog("跳过 " + fam + " : 修复冷却中 (上次 " + st.LastRepair.ToString("HH:mm") + ")");
                            return;
                        }
                    }
                }
                RepairFamily(fam);
            }
            catch (Exception ex) { WriteLog("[x] 处理 " + fam + " 异常: " + ex.Message); }
        }

        // ---------------- 修复阶梯 ----------------

        private void RepairFamily(string fam)
        {
            SetBusy(true);
            try
            {
                // 1) 查包状态
                List<string> info = PsLines(PsGetInfo(fam), 60);
                if (info == null || info.Count == 0 || info[0].Trim() == "NOTINSTALLED" || info[0].Trim() == "EMPTYLOC")
                {
                    WriteLog("跳过 " + fam + " : 包未安装或无安装位置 (残留事件)");
                    MarkManualForever(fam);
                    return;
                }
                string loc = info[0].Trim();
                string fullname = info.Count > 1 ? info[1].Trim() : fam;
                bool framework = info.Count > 2 && info[2].Trim() == "FRAMEWORK";
                if (framework)
                {
                    WriteLog("跳过 " + fam + " : 框架包, 不适用应用修复");
                    MarkManualForever(fam);
                    return;
                }

                // 2) 在运行 = 拉得起 = 健康
                string rcRaw = PsText(PsRunningCount(loc), 60);
                int rc; int.TryParse(rcRaw == null ? "-1" : rcRaw.Trim(), out rc);
                if (rc > 0)
                {
                    WriteLog("[ok] " + fam + " 当前有进程在运行, 视为健康, 不修复");
                    return;
                }

                // 3) 决定阶梯级别 (1/2/3, 超过3转人工)
                int rung = 1; PkgState st = GetState(fam);
                if (st != null && st.LastRepair > DateTime.MinValue) rung = st.Rung + 1;
                if (rung > 3)
                {
                    st.Rung = 3; st.ManualUntil = DateTime.Now.AddHours(ManualHours); st.NotifiedManual = false;
                    SaveState();
                    WriteLog("[!] " + fam + " 三级修复均无效, 转人工处理 " + ManualHours + " 小时");
                    DoNotify("需人工处理: " + fam, "自动修复 3 轮无效。请打开微软商店重装该应用, 或运行『一键修复商店应用』。", "warn");
                    return;
                }

                string result;
                if (rung == 1)
                {
                    WriteLog("修复 " + fam + " 第1级: 重注册 ...");
                    result = PsText(PsRegister(fam), 180);
                }
                else if (rung == 2)
                {
                    WriteLog("修复 " + fam + " 第2级: 结束进程 + 重建数据目录 + 重注册 ...");
                    PsText(PsKillProcs(loc), 60);
                    Thread.Sleep(2000);
                    string backup = RenameDataDir(fam);
                    if (backup != null) WriteLog("    旧数据目录已备份: " + backup);
                    result = PsText(PsRegister(fam), 180);
                }
                else
                {
                    WriteLog("修复 " + fam + " 第3级: 重置应用 (Reset-AppxPackage) ...");
                    result = PsText(PsReset(fam), 300);
                }

                st = GetState(fam);
                st.Rung = rung; st.LastRepair = DateTime.Now; st.NotifiedManual = false;
                SaveState();

                string r = result == null ? "PS-TIMEOUT" : result.Trim();
                if (r == "REGISTER-OK" || r == "RESET-OK")
                {
                    WriteLog("[ok] " + fam + " 第" + rung + "级修复完成 (" + r + "), 等待下次启动验证");
                    DoNotify("已自动修复: " + fam, "第 " + rung + " 级修复 (" + (rung == 1 ? "重注册" : rung == 2 ? "重建数据目录" : "重置应用") + ") 已完成。若仍打不开会自动升级手段。", "info");
                }
                else if (r == "RESET-UNAVAILABLE")
                {
                    WriteLog("[!] " + fam + " : 系统无 Reset-AppxPackage, 第3级不可用");
                }
                else
                {
                    WriteLog("[x] " + fam + " 第" + rung + "级返回: " + r);
                }
            }
            finally { SetBusy(false); }
        }

        private void MarkManualForever(string fam)
        {
            PkgState st = GetState(fam);
            st.ManualUntil = DateTime.MaxValue; st.NotifiedManual = true;
            SaveState();
        }

        private PkgState GetState(string fam)
        {
            lock (gate)
            {
                PkgState st;
                if (!states.TryGetValue(fam, out st)) { st = new PkgState(); states[fam] = st; }
                return st;
            }
        }

        private string RenameDataDir(string fam)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                    , Path.Combine("Packages", fam));
                if (!Directory.Exists(dir)) return null;
                string bak = dir + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                Directory.Move(dir, bak);
                return Path.GetFileName(bak);
            }
            catch (Exception ex) { WriteLog("[!] 数据目录改名失败: " + ex.Message); return null; }
        }

        // ---------------- PowerShell 执行器 ----------------

        private string PsText(string script, int timeoutSec)
        {
            List<string> lines = PsLines(script, timeoutSec);
            return lines == null ? null : (lines.Count > 0 ? lines[lines.Count - 1] : "");
        }

        private List<string> PsLines(string script, int timeoutSec)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "powershell.exe";
                psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand "
                    + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    StringBuilder so = new StringBuilder();
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (so) so.AppendLine(e.Data); };
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutSec * 1000))
                    {
                        try { p.Kill(); } catch { }
                        WriteLog("[!] PowerShell 超时 (" + timeoutSec + "s) 被终止");
                        return null;
                    }
                    string all = so.ToString();
                    return all.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                }
            }
            catch (Exception ex) { WriteLog("[!] PowerShell 执行异常: " + ex.Message); return null; }
        }

        // ---- 各修复步骤的 PS 脚本 ----

        private static string Q(string s) { return s.Replace("'", "''"); }

        private static string PsGetInfo(string fam)
        {
            return "$p = Get-AppxPackage | Where-Object { $_.PackageFamilyName -eq '" + Q(fam) + "' } | Select-Object -First 1; "
                 + "if (-not $p) { 'NOTINSTALLED' } "
                 + "elseif ([string]::IsNullOrEmpty('' + $p.InstallLocation)) { 'EMPTYLOC'; $p.PackageFullName } "
                 + "else { '' + $p.InstallLocation; $p.PackageFullName; if ($p.IsFramework) { 'FRAMEWORK' } else { 'APP' } }";
        }

        private static string PsRegister(string fam)
        {
            return "$ErrorActionPreference = 'Stop'; "
                 + "$p = Get-AppxPackage | Where-Object { $_.PackageFamilyName -eq '" + Q(fam) + "' } | Select-Object -First 1; "
                 + "if (-not $p) { 'NOTINSTALLED' } "
                 + "else { $m1 = Join-Path $p.InstallLocation 'AppXManifest.xml'; "
                 + "$m2 = Join-Path $p.InstallLocation 'AppxMetadata\\AppxBundleManifest.xml'; "
                 + "try { "
                 + "if (Test-Path $m1) { Add-AppxPackage -DisableDevelopmentMode -Register $m1; 'REGISTER-OK' } "
                 + "elseif (Test-Path $m2) { Add-AppxPackage -DisableDevelopmentMode -Register $m2; 'REGISTER-OK' } "
                 + "else { 'NOMANIFEST' } } "
                 + "catch { 'PSERROR: ' + ($_.Exception.Message -replace '\\r?\\n', ' ') } }";
        }

        private static string PsKillProcs(string loc)
        {
            return "Get-Process | Where-Object { try { $_.Path -like '" + Q(loc) + "\\*' } catch { $false } } | Stop-Process -Force -ErrorAction SilentlyContinue; 'KILL-OK'";
        }

        private static string PsRunningCount(string loc)
        {
            return "@(Get-Process | Where-Object { try { $_.Path -like '" + Q(loc) + "\\*' } catch { $false } }).Count";
        }

        private static string PsReset(string fam)
        {
            return "$p = Get-AppxPackage | Where-Object { $_.PackageFamilyName -eq '" + Q(fam) + "' } | Select-Object -First 1; "
                 + "if (-not $p) { 'NOTINSTALLED' } "
                 + "elseif (-not (Get-Command Reset-AppxPackage -ErrorAction SilentlyContinue)) { 'RESET-UNAVAILABLE' } "
                 + "else { try { Reset-AppxPackage -Package $p -ErrorAction Stop; 'RESET-OK' } "
                 + "catch { 'PSERROR: ' + ($_.Exception.Message -replace '\\r?\\n', ' ') } }";
        }

        // 用 Get-WinEvent 扫描失败记录 -> { 包族: 次数 }
        private Dictionary<string, int> SweepFamilies(int minutes)
        {
            string script =
                "$since = (Get-Date).AddMinutes(-" + minutes + "); $f = @{}; "
                + "$ev1 = Get-WinEvent -FilterHashtable @{LogName='" + LogAppModel + "'; Level=2; StartTime=$since} -ErrorAction SilentlyContinue; "
                + "$ev2 = Get-WinEvent -FilterHashtable @{LogName='" + LogTWinUI + "'; Id=5973; StartTime=$since} -ErrorAction SilentlyContinue; "
                + "foreach ($e in @($ev1) + @($ev2)) { "
                + "  $k = $null; "
                + "  if ($e.Message -match '([A-Za-z0-9.\\-]+_\\d+(?:\\.\\d+)*_(?:x64|arm64|x86|neutral)__[A-Za-z0-9]+)') { $k = $Matches[1] } "
                + "  elseif ($e.Message -match '([A-Za-z0-9.]+_[A-Za-z0-9]{13})') { $k = $Matches[1] } "
                + "  if ($k) { if (-not $f.ContainsKey($k)) { $f[$k] = 0 }; $f[$k]++ } } "
                + "if ($f.Count -eq 0) { 'CLEAN' } else { foreach ($k in $f.Keys) { $k + \"`t\" + $f[$k] } }";
            List<string> lines = PsLines(script, 120);
            Dictionary<string, int> r = new Dictionary<string, int>();
            if (lines == null) return r;
            foreach (string line in lines)
            {
                string l = line.Trim();
                if (l == "CLEAN" || l.Length == 0) continue;
                string[] parts = l.Split('\t');
                string fam = FullToFam(parts[0].Trim());
                int c; int.TryParse(parts[parts.Length - 1].Trim(), out c);
                if (r.ContainsKey(fam)) r[fam] += c; else r[fam] = c;
            }
            return r;
        }

        // ---------------- 状态文件 ----------------

        private void LoadState()
        {
            lock (gate)
            {
                states.Clear();
                try
                {
                    if (!File.Exists(StatePath)) return;
                    foreach (string line in File.ReadAllLines(StatePath))
                    {
                        string[] p = line.TrimStart('\uFEFF').Split('\t');
                        if (p.Length < 4) continue;
                        long t1, t2; int rung;
                        PkgState st = new PkgState();
                        int.TryParse(p[1], out rung);
                        long.TryParse(p[2], out t1);
                        long.TryParse(p[3], out t2);
                        st.Rung = rung;
                        if (t1 > 0) st.LastRepair = new DateTime(t1);
                        if (t2 > 0) st.ManualUntil = new DateTime(t2);
                        if (p.Length > 4 && p[4] == "1") st.NotifiedManual = true;
                        states[p[0]] = st;
                    }
                }
                catch { }
            }
        }

        private void SaveState()
        {
            lock (gate)
            {
                try
                {
                    StringBuilder sb = new StringBuilder();
                    foreach (KeyValuePair<string, PkgState> kv in states)
                        sb.AppendLine(kv.Key + "\t" + kv.Value.Rung + "\t" + kv.Value.LastRepair.Ticks + "\t"
                            + kv.Value.ManualUntil.Ticks + "\t" + (kv.Value.NotifiedManual ? "1" : "0"));
                    File.WriteAllText(StatePath, sb.ToString(), Utf8NoBom);
                }
                catch { }
            }
        }

        // ---------------- 其他 ----------------

        private void SetBusy(bool b)
        {
            bool changed = false;
            lock (gate) { if (busy != b) { busy = b; changed = true; } }
            if (changed) { Action<bool> h = BusyChanged; if (h != null) h(b); }
        }

        private void DoNotify(string title, string msg, string kind)
        {
            if (notify != null) notify(title, msg, kind);
        }

        internal void WriteLog(string msg)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg;
            try
            {
                lock (gate)
                {
                    string f = LogFile;
                    if (File.Exists(f)) File.AppendAllText(f, line + Environment.NewLine, Utf8NoBom);
                    else File.WriteAllText(f, line + Environment.NewLine, Utf8Bom); // 新文件带 BOM, 记事本/日志查看器不乱码
                }
            }
            catch { }
            try { Console.WriteLine(line); } catch { }
        }

        private static readonly Encoding Utf8Bom = new UTF8Encoding(true);
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    }
}
