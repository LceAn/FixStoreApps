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
using System.Xml.Linq;
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

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        internal const string MainTitle = "商店应用守护 - StoreAppGuard";

        [STAThread]
        internal static void Main(string[] args)
        {
            // ---- 命令行模式 ----
            bool once = false, viewerOnly = false, showOnly = false; int days = 3;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--once" || args[i] == "/once") once = true;
                if (args[i] == "--logviewer" || args[i] == "/logviewer") viewerOnly = true;
                if (args[i] == "--show" || args[i] == "/show") showOnly = true;
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
            if (viewerOnly) // 只打开日志查看器窗口 (调试/菜单用)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new LogViewerForm(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs")));
                return;
            }
            if (showOnly) // 已有实例在跑: 唤起它的主窗口后退出
            {
                IntPtr h = FindWindow(null, MainTitle);
                if (h != IntPtr.Zero)
                {
                    ShowWindow(h, 9); // SW_RESTORE
                    SetForegroundWindow(h);
                }
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

    // ==================== 公共工具 ====================
    internal static class Util
    {
        // ---- 开机自启 (HKCU Run, 无需管理员) ----
        internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        internal const string RunName = "StoreAppGuard";

        internal static bool AutoStartEnabled
        {
            get
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                    {
                        if (k == null) return false;
                        object v = k.GetValue(RunName);
                        return v != null && File.Exists(v.ToString().Trim('"'));
                    }
                }
                catch { return false; }
            }
        }

        internal static void SetAutoStart(bool on)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(RunName, false);
            }
        }

        internal static void OpenDir(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", dir);
            }
            catch { }
        }
    }

    // ==================== 托盘 + 主窗口 上下文 ====================
    internal sealed class GuardContext : ApplicationContext
    {
        private readonly NotifyIcon tray;
        private readonly Control syncCtrl = new Control();   // 跨线程通知的同步对象
        private readonly Engine engine;
        private readonly Icon icoGreen, icoOrange;
        private readonly ToolStripMenuItem miAutoStart;
        private readonly MainForm mainForm;
        private LogViewerForm viewer;   // 独立日志窗口单例
        private bool hiddenTipShown;

        internal GuardContext()
        {
            IntPtr h = syncCtrl.Handle; // 强制创建句柄, 供 BeginInvoke
            icoGreen = MakeIcon(Color.FromArgb(46, 160, 67), true);
            icoOrange = MakeIcon(Color.FromArgb(230, 140, 30), false);

            engine = new Engine(UiNotify);
            engine.BusyChanged += Engine_BusyChanged;

            mainForm = new MainForm(engine, this);

            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("显示主界面", null, delegate { ShowMain(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("立即检查修复", null, delegate { RunBg(delegate { engine.RunSweepOnce(3); }, "手动扫描"); }));
            menu.Items.Add(new ToolStripMenuItem("查看日志", null, delegate { ShowViewer(); }));
            menu.Items.Add(new ToolStripMenuItem("打开日志目录", null, delegate { Util.OpenDir(engine.LogDir); }));
            miAutoStart = new ToolStripMenuItem("开机自动启动", null, MenuAutoStartToggle);
            miAutoStart.Checked = Util.AutoStartEnabled;
            menu.Items.Add(miAutoStart);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("关于 StoreAppGuard", null, delegate {
                MessageBox.Show("商店应用守护进程 StoreAppGuard v1.2\r\n\r\n"
                    + "实时监听商店应用激活失败事件, 自动执行修复阶梯:\r\n"
                    + "  1. 重注册  2. 重建数据目录+重注册  3. 重置应用\r\n"
                    + "三级无效时通知人工重装 (24 小时内不再自动尝试)。\r\n\r\n"
                    + "关闭窗口 = 最小化到托盘继续守护; 右键托盘图标可退出。\r\n\r\n"
                    + "源码: https://github.com/LceAn/FixStoreApps",
                    "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }));
            menu.Items.Add(new ToolStripMenuItem("退出", null, delegate { ExitAll(); }));

            tray = new NotifyIcon();
            tray.Icon = icoGreen;
            tray.Text = "商店应用守护 - 运行中";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowMain(); };

            engine.Start();
            mainForm.Show();
        }

        internal void ShowMain()
        {
            if (mainForm.IsDisposed) return;
            if (!mainForm.Visible) mainForm.Show();
            mainForm.Activate();
        }

        // 主窗口 X 按钮 -> 隐藏到托盘继续守护
        internal void HideToTray()
        {
            mainForm.Hide();
            if (!hiddenTipShown)
            {
                hiddenTipShown = true;
                tray.ShowBalloonTip(4000, "仍在后台守护",
                    "窗口已最小化到托盘, 商店应用故障监控与自动修复继续运行。双击托盘图标可重新打开。", ToolTipIcon.Info);
            }
        }

        internal void ExitAll()
        {
            engine.Stop();
            if (mainForm != null && !mainForm.IsDisposed) mainForm.RealClose = true;
            try { if (viewer != null && !viewer.IsDisposed) viewer.Close(); } catch { }
            tray.Visible = false;
            ExitThread();
        }

        internal void ShowViewer()
        {
            try
            {
                if (viewer != null && !viewer.IsDisposed) { viewer.Show(); viewer.Activate(); return; }
                viewer = new LogViewerForm(engine.LogDir);
                viewer.Show();
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "打开日志失败"); }
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

        private void MenuAutoStartToggle(object s, EventArgs e)
        {
            try { Util.SetAutoStart(!miAutoStart.Checked); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "设置失败"); }
            miAutoStart.Checked = Util.AutoStartEnabled;
            mainForm.SyncAutoStart();
        }

        // ---- 运行时绘制托盘图标 (绿色对勾=正常, 橙色=修复中) ----
        internal static Icon MakeIcon(Color back, bool check)
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

    // ==================== 主窗口 ====================
    internal sealed class MainForm : Form
    {
        private readonly Engine engine;
        private readonly GuardContext owner;
        private readonly Icon icoGreen, icoOrange;

        private Label lblDot, lblTitle, lblSub;
        private DataGridView grid;
        private RichTextBox rtb;
        private CheckBox chkAuto;
        private Button btnSweep, btnViewer, btnExit;
        private ToolStripStatusLabel stripLbl;
        private System.Windows.Forms.Timer uiTimer;
        private DateTime logStamp = DateTime.MinValue;
        private int gridTick;
        internal bool RealClose; // true = 真退出, 否则关窗=隐藏到托盘

        internal MainForm(Engine eng, GuardContext own)
        {
            engine = eng;
            owner = own;
            icoGreen = GuardContext.MakeIcon(Color.FromArgb(46, 160, 67), true);
            icoOrange = GuardContext.MakeIcon(Color.FromArgb(230, 140, 30), false);

            Text = Program.MainTitle;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(980, 640);
            MinimumSize = new Size(720, 460);
            Icon = icoGreen;

            BuildTopBar();
            BuildGrid();
            BuildLogPanel();
            BuildStrip();
            BuildBottomBar();

            // 引擎事件 -> UI 线程刷新
            engine.BusyChanged += delegate(bool busy)
            {
                SafeInvoke(delegate { ApplyBusy(busy); });
            };
            engine.StateChanged += delegate
            {
                SafeInvoke(delegate { RefreshGrid(); });
            };

            uiTimer = new System.Windows.Forms.Timer();
            uiTimer.Interval = 2000;
            uiTimer.Tick += delegate
            {
                LoadTail();
                gridTick++;
                if (gridTick >= 5) { gridTick = 0; RefreshGrid(); } // 每 10 秒刷一次应用状态
                SyncStrip();
            };
            uiTimer.Start();

            Load += delegate { RefreshGrid(); LoadTail(); SyncStrip(); };
            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (!RealClose) { e.Cancel = true; owner.HideToTray(); }
            };
        }

        private void SafeInvoke(MethodInvoker a)
        {
            try
            {
                if (IsDisposed) return;
                if (InvokeRequired) BeginInvoke(a);
                else a();
            }
            catch { }
        }

        // ---- 顶部状态栏 ----
        private void BuildTopBar()
        {
            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 64;
            top.Padding = new Padding(14, 8, 14, 6);
            top.Paint += delegate(object s, PaintEventArgs e)
            {
                e.Graphics.DrawLine(Pens.Gainsboro, 0, top.Height - 1, top.Width, top.Height - 1);
            };

            lblDot = new Label();
            lblDot.Text = "●";
            lblDot.Font = new Font("Microsoft YaHei UI", 22f, FontStyle.Bold);
            lblDot.ForeColor = Color.FromArgb(46, 160, 67);
            lblDot.AutoSize = true;
            lblDot.Location = new Point(16, 8);

            lblTitle = new Label();
            lblTitle.Text = "商店应用守护";
            lblTitle.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            lblTitle.AutoSize = true;
            lblTitle.Location = new Point(66, 10);

            lblSub = new Label();
            lblSub.Text = "监控中 · 等待事件";
            lblSub.Font = new Font("Microsoft YaHei UI", 9.5f);
            lblSub.ForeColor = Color.DimGray;
            lblSub.AutoSize = true;
            lblSub.Location = new Point(68, 36);

            btnSweep = new Button();
            btnSweep.Text = "立即检查修复";
            btnSweep.Width = 112;
            btnSweep.Height = 30;
            btnSweep.Top = 16;

            chkAuto = new CheckBox();
            chkAuto.Text = "开机自启";
            chkAuto.Width = 84;
            chkAuto.Height = 30;
            chkAuto.Top = 18;

            top.Controls.Add(lblDot);
            top.Controls.Add(lblTitle);
            top.Controls.Add(lblSub);
            top.Controls.Add(btnSweep);
            top.Controls.Add(chkAuto);
            top.Resize += delegate
            {
                chkAuto.Left = top.Width - chkAuto.Width - 16;
                btnSweep.Left = chkAuto.Left - btnSweep.Width - 10;
            };

            btnSweep.Click += delegate
            {
                btnSweep.Enabled = false;
                lblSub.Text = "正在扫描最近 3 天记录 ...";
                Task.Factory.StartNew(delegate
                {
                    try { engine.RunSweepOnce(3); }
                    catch { }
                    SafeInvoke(delegate { btnSweep.Enabled = true; });
                });
            };
            chkAuto.CheckedChanged += delegate
            {
                try { Util.SetAutoStart(chkAuto.Checked); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "设置失败"); }
                chkAuto.Checked = Util.AutoStartEnabled;
            };
            chkAuto.Checked = Util.AutoStartEnabled;

            Controls.Add(top);
        }

        private void ApplyBusy(bool busy)
        {
            lblDot.ForeColor = busy ? Color.FromArgb(230, 140, 30) : Color.FromArgb(46, 160, 67);
            lblSub.Text = busy ? "正在修复 ..." : "监控中 · 等待事件";
            Icon = busy ? icoOrange : icoGreen;
        }

        // ---- 应用状态表格 ----
        private void BuildGrid()
        {
            Label cap = new Label();
            cap.Text = "  监控中的应用 (出现过的故障与修复状态)";
            cap.Dock = DockStyle.Top;
            cap.Height = 26;
            cap.TextAlign = ContentAlignment.MiddleLeft;
            cap.BackColor = Color.WhiteSmoke;
            cap.ForeColor = Color.DimGray;

            grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.BackgroundColor = Color.White;
            grid.BorderStyle = BorderStyle.None;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.GridColor = Color.Gainsboro;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = 30;
            grid.RowTemplate.Height = 34;
            grid.Columns.Add("app", "应用");
            grid.Columns["app"].FillWeight = 26;
            grid.Columns.Add("state", "状态");
            grid.Columns["state"].FillWeight = 22;
            grid.Columns.Add("rung", "修复级别");
            grid.Columns["rung"].FillWeight = 12;
            grid.Columns.Add("lastfix", "上次修复");
            grid.Columns["lastfix"].FillWeight = 16;
            grid.Columns.Add("running", "运行中");
            grid.Columns["running"].FillWeight = 10;

            // 图标列 (放最前, 后台异步填充)
            DataGridViewImageColumn imgCol = new DataGridViewImageColumn(false);
            imgCol.HeaderText = "";
            imgCol.Name = "icon";
            imgCol.Width = 42;
            imgCol.ImageLayout = DataGridViewImageCellLayout.Zoom;
            imgCol.SortMode = DataGridViewColumnSortMode.NotSortable;
            imgCol.DefaultCellStyle.NullValue = null; // 值为 null 时留白, 不画红色错误占位图
            grid.Columns.Insert(0, imgCol);

            Panel gp = new Panel();
            gp.Dock = DockStyle.Fill;
            gp.Padding = new Padding(14, 4, 14, 2);
            gp.Controls.Add(grid);
            gp.Controls.Add(cap); // dock 顺序: 后加的 Top 在内层

            split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            split.SplitterDistance = 220;
            split.Panel1.Controls.Add(gp);
            Controls.Add(split);
            split.BringToFront();
        }

        private SplitContainer split;

        // ---- 实时日志面板 ----
        private void BuildLogPanel()
        {
            Label cap = new Label();
            cap.Text = "  实时日志";
            cap.Dock = DockStyle.Top;
            cap.Height = 26;
            cap.TextAlign = ContentAlignment.MiddleLeft;
            cap.BackColor = Color.WhiteSmoke;
            cap.ForeColor = Color.DimGray;

            rtb = new RichTextBox();
            rtb.Dock = DockStyle.Fill;
            rtb.ReadOnly = true;
            rtb.WordWrap = false;
            rtb.BackColor = Color.White;
            rtb.Font = new Font("Consolas", 9f);
            rtb.DetectUrls = false;
            rtb.BorderStyle = BorderStyle.None;

            Panel lp = new Panel();
            lp.Dock = DockStyle.Fill;
            lp.Padding = new Padding(14, 2, 14, 6);
            lp.Controls.Add(rtb);
            lp.Controls.Add(cap);

            split.Panel2.Controls.Add(lp);
        }

        // ---- 底部按钮栏 + 状态条 ----
        private void BuildStrip()
        {
            StatusStrip strip = new StatusStrip();
            stripLbl = new ToolStripStatusLabel("就绪");
            strip.Items.Add(stripLbl);
            Controls.Add(strip);
        }

        private void BuildBottomBar()
        {
            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = 46;
            bar.FlowDirection = FlowDirection.LeftToRight;
            bar.Padding = new Padding(14, 8, 14, 4);

            btnViewer = new Button();
            btnViewer.Text = "独立日志窗口";
            btnViewer.AutoSize = true;
            btnViewer.Padding = new Padding(6, 2, 6, 2);
            btnViewer.Click += delegate { owner.ShowViewer(); };

            Button btnDir = new Button();
            btnDir.Text = "打开日志目录";
            btnDir.AutoSize = true;
            btnDir.Padding = new Padding(6, 2, 6, 2);
            btnDir.Click += delegate { Util.OpenDir(engine.LogDir); };

            btnExit = new Button();
            btnExit.Text = "退出守护";
            btnExit.AutoSize = true;
            btnExit.Padding = new Padding(6, 2, 6, 2);
            btnExit.ForeColor = Color.Firebrick;
            btnExit.Click += delegate { owner.ExitAll(); };

            Label hint = new Label();
            hint.Text = "关闭窗口 = 最小化到托盘, 守护不中断";
            hint.AutoSize = true;
            hint.ForeColor = Color.DimGray;
            hint.Padding = new Padding(14, 6, 0, 0);

            bar.Controls.Add(btnViewer);
            bar.Controls.Add(btnDir);
            bar.Controls.Add(btnExit);
            bar.Controls.Add(hint);
            Controls.Add(bar);
        }

        // ---- 刷新逻辑 ----

        internal void SyncAutoStart()
        {
            SafeInvoke(delegate { chkAuto.Checked = Util.AutoStartEnabled; });
        }

        // ---- 应用图标/友好名称 (后台提取, 缓存) ----
        private readonly object metaGate = new object();
        private readonly Dictionary<string, Image> iconCache = new Dictionary<string, Image>();
        private readonly Dictionary<string, string> nameCache = new Dictionary<string, string>();
        private readonly HashSet<string> metaPending = new HashSet<string>();
        private readonly HashSet<string> metaDone = new HashSet<string>();   // 防止失败后无限重试

        // 无 IO 的名称美化回退: MSTeams_8wek -> MSTeams, Microsoft.WindowsTerminal_.. -> WindowsTerminal
        private static string PrettyName(string family)
        {
            string n = family.Split('_')[0];
            if (n.StartsWith("MicrosoftWindows.")) return n.Substring("MicrosoftWindows.".Length);
            if (n.StartsWith("Microsoft.")) return n.Substring("Microsoft.".Length);
            return n;
        }

        private void QueueMeta(Engine.PkgView v)
        {
            lock (metaGate)
            {
                if (v.InstallLocation == null || metaDone.Contains(v.Family) || metaPending.Contains(v.Family)) return;
                metaPending.Add(v.Family);
            }
            string fam = v.Family, loc = v.InstallLocation, full = v.FullName;
            Task.Factory.StartNew(delegate
            {
                Image ico = null; string name = null;
                try { ico = ExtractAppIcon(loc); } catch { }
                try { name = LookupDisplayName(full, fam); } catch { }
                lock (metaGate)
                {
                    metaPending.Remove(fam);
                    metaDone.Add(fam);
                    if (ico != null) iconCache[fam] = ico;
                    if (name != null) nameCache[fam] = name;
                }
                SafeInvoke(RefreshGrid);
            });
        }

        // 从包清单找主程序 EXE -> 取它的图标
        private static Image ExtractAppIcon(string loc)
        {
            try
            {
                string exe = null;
                string m1 = Path.Combine(loc, "AppxManifest.xml");
                string m2 = Path.Combine(loc, "AppxMetadata", "AppxBundleManifest.xml");
                string manifest = File.Exists(m1) ? m1 : (File.Exists(m2) ? m2 : null);
                if (manifest != null)
                {
                    XDocument x = XDocument.Load(manifest);
                    foreach (XElement el in x.Descendants())
                    {
                        if (el.Name.LocalName != "Application") continue;
                        XAttribute a = el.Attribute("Executable");
                        if (a != null && !string.IsNullOrEmpty(a.Value)) { exe = a.Value; break; }
                    }
                }
                if (exe == null)
                {
                    string e = null;
                    foreach (string f in Directory.GetFiles(loc, "*.exe")) { e = f; break; }
                    if (e != null) exe = Path.GetFileName(e);
                }
                if (exe == null) return null;
                string full = Path.Combine(loc, exe);
                if (!File.Exists(full)) return null;
                using (Icon ic = Icon.ExtractAssociatedIcon(full))
                {
                    if (ic == null) return null;
                    using (Bitmap b = ic.ToBitmap())
                    {
                        Bitmap small = new Bitmap(b, 24, 24);
                        small.SetResolution(b.HorizontalResolution, b.VerticalResolution);
                        return small;
                    }
                }
            }
            catch { return null; }
        }

        // 显示名: 系统仓库注册表 -> ms-resource 时回退到包名美化
        private static string LookupDisplayName(string fullName, string family)
        {
            string disp = null;
            if (!string.IsNullOrEmpty(fullName))
            {
                try
                {
                    using (RegistryKey k = Registry.ClassesRoot.OpenSubKey(
                        "Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\Repository\\Packages\\" + fullName))
                    {
                        if (k != null)
                        {
                            object v = k.GetValue("DisplayName");
                            if (v != null) disp = v.ToString();
                        }
                    }
                }
                catch { }
            }
            if (string.IsNullOrEmpty(disp) || disp.StartsWith("ms-resource") || disp.StartsWith("@{")) disp = null;
            if (disp != null) return disp;
            // 回退: 包族名前段, 去掉 Microsoft. 前缀
            string n = family.Split('_')[0];
            if (n.StartsWith("Microsoft.")) n = n.Substring("Microsoft.".Length);
            if (n.StartsWith("MicrosoftWindows.")) n = n.Substring("MicrosoftWindows.".Length);
            return n;
        }

        private void RefreshGrid()
        {
            try
            {
                List<Engine.PkgView> snap = engine.GetStateSnapshot();
                Process[] procs = Process.GetProcesses();
                DateTime now = DateTime.Now;
                grid.Rows.Clear();
                int todayFix = 0;
                foreach (Engine.PkgView v in snap)
                {
                    if (v.LastRepair.Date == now.Date) todayFix++;

                    string status; Color c;
                    if (v.ManualUntil == DateTime.MaxValue) { status = "已忽略(残留记录)"; c = Color.Gray; }
                    else if (v.ManualUntil > now) { status = "待人工处理 至 " + v.ManualUntil.ToString("MM-dd HH:mm"); c = Color.Firebrick; }
                    else if (v.LastRepair != DateTime.MinValue && now - v.LastRepair < TimeSpan.FromMinutes(15)) { status = "修复冷却中"; c = Color.DarkOrange; }
                    else if (v.Rung > 0) { status = "监控中·已修" + v.Rung + "级"; c = Color.Green; }
                    else { status = "监控中"; c = Color.Green; }

                    string run = "-";
                    if (v.InstallLocation != null)
                    {
                        foreach (Process p in procs)
                        {
                            bool hit = false;
                            try
                            {
                                if (p.MainModule != null && p.MainModule.FileName != null
                                    && p.MainModule.FileName.StartsWith(v.InstallLocation, StringComparison.OrdinalIgnoreCase)) hit = true;
                            }
                            catch { } // 系统/提权进程读不到 MainModule, 忽略
                            if (hit) { run = "●"; break; }
                        }
                    }

                    Image ico = null; string name;
                    lock (metaGate)
                    {
                        iconCache.TryGetValue(v.Family, out ico);
                        if (!nameCache.TryGetValue(v.Family, out name)) name = PrettyName(v.Family);
                    }
                    QueueMeta(v); // 没有图标时后台补

                    string lastfix = v.LastRepair == DateTime.MinValue ? "-" : v.LastRepair.ToString("MM-dd HH:mm");
                    int i = grid.Rows.Add(ico, name, status, v.Rung == 0 ? "-" : "第" + v.Rung + "级", lastfix, run);
                    grid.Rows[i].Cells[1].ToolTipText = v.Family; // 悬停看包族名
                    grid.Rows[i].Cells[1].Style.ForeColor = Color.FromArgb(30, 30, 30);
                    grid.Rows[i].Cells[2].Style.ForeColor = c;
                    grid.Rows[i].Cells[5].Style.ForeColor = run == "●" ? Color.Green : Color.DimGray;
                    if (v.ManualUntil == DateTime.MaxValue) grid.Rows[i].DefaultCellStyle.ForeColor = Color.Gray;
                }
                grid.ClearSelection();
                curTodayFix = todayFix;
                curWatched = snap.Count;
            }
            catch { }
        }

        private int curTodayFix, curWatched;

        private void SyncStrip()
        {
            stripLbl.Text = string.Format("今日修复 {0} 次 · 监控 {1} 个应用 · 事件监听 + 每 5 分钟巡检 · v1.2",
                curTodayFix, curWatched);
        }

        // 尾随今天日志的最后 400 行
        private void LoadTail()
        {
            try
            {
                string path = Path.Combine(engine.LogDir, "guard-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                if (!File.Exists(path)) return;
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (stamp == logStamp) return;
                logStamp = stamp;

                rtb.Text = "";
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                int start = Math.Max(0, lines.Length - 400);
                for (int i = start; i < lines.Length; i++)
                {
                    string l = lines[i];
                    if (l.Length == 0) continue;
                    Color c = Color.Black;
                    if (l.IndexOf("[ok]") >= 0) c = Color.FromArgb(0, 128, 0);
                    else if (l.IndexOf("[x]") >= 0) c = Color.FromArgb(200, 30, 30);
                    else if (l.IndexOf("[!]") >= 0 || l.IndexOf("人工") >= 0) c = Color.FromArgb(190, 110, 0);
                    else if (l.IndexOf("修复") >= 0 || l.IndexOf("事件:") >= 0 || l.IndexOf("巡检") >= 0) c = Color.FromArgb(0, 90, 160);
                    rtb.SelectionStart = rtb.TextLength;
                    rtb.SelectionLength = 0;
                    rtb.SelectionColor = c;
                    rtb.AppendText(l + "\n");
                }
                rtb.SelectionColor = Color.Black;
                rtb.SelectionStart = rtb.TextLength;
                rtb.ScrollToCaret();
            }
            catch { }
        }
    }

    // ==================== 内置日志查看器 ====================
    internal sealed class LogViewerForm : Form
    {
        private readonly string logDir;
        private ComboBox cboFiles;
        private RichTextBox rtb;
        private CheckBox chkFollow;
        private System.Windows.Forms.Timer tmr;
        private Label lblStatus;
        private DateTime lastStamp = DateTime.MinValue;
        private bool loading;

        internal LogViewerForm(string dir)
        {
            logDir = dir;
            Text = "StoreAppGuard 日志";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(880, 560);
            MinimumSize = new Size(560, 320);
            try { Icon = GuardContext.MakeIcon(Color.FromArgb(46, 160, 67), true); } catch { }

            cboFiles = new ComboBox();
            cboFiles.DropDownStyle = ComboBoxStyle.DropDownList;
            cboFiles.Width = 210;
            cboFiles.SelectedIndexChanged += delegate { lastStamp = DateTime.MinValue; LoadLog(true); };

            Button btnRefresh = new Button();
            btnRefresh.Text = "刷新";
            btnRefresh.AutoSize = true;
            btnRefresh.Click += delegate { LoadFileList(); LoadLog(true); };

            Button btnCopy = new Button();
            btnCopy.Text = "复制全部";
            btnCopy.AutoSize = true;
            btnCopy.Click += delegate { try { Clipboard.SetText(rtb.Text); } catch { } };

            Button btnDir = new Button();
            btnDir.Text = "打开目录";
            btnDir.AutoSize = true;
            btnDir.Click += delegate
            {
                try { if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir); Process.Start("explorer.exe", logDir); } catch { }
            };

            chkFollow = new CheckBox();
            chkFollow.Text = "自动跟随最新";
            chkFollow.Checked = true;
            chkFollow.AutoSize = true;

            FlowLayoutPanel top = new FlowLayoutPanel();
            top.Dock = DockStyle.Top;
            top.Height = 42;
            top.Padding = new Padding(8, 8, 8, 0);
            top.Controls.AddRange(new Control[] { cboFiles, btnRefresh, btnCopy, btnDir, chkFollow });

            lblStatus = new Label();
            lblStatus.Dock = DockStyle.Bottom;
            lblStatus.Height = 26;
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            lblStatus.Padding = new Padding(8, 0, 0, 0);
            lblStatus.ForeColor = Color.DimGray;
            lblStatus.Text = "就绪";

            rtb = new RichTextBox();
            rtb.Dock = DockStyle.Fill;
            rtb.ReadOnly = true;
            rtb.WordWrap = false;
            rtb.BackColor = Color.White;
            rtb.Font = new Font("Consolas", 9.5f);
            rtb.DetectUrls = false;

            Controls.Add(rtb);
            Controls.Add(top);
            Controls.Add(lblStatus);

            tmr = new System.Windows.Forms.Timer();
            tmr.Interval = 3000;
            tmr.Tick += delegate { LoadLog(false); };
            tmr.Start();

            Load += delegate { LoadFileList(); };
            FormClosed += delegate { tmr.Stop(); };
        }

        // 列出全部日志文件, 新的在前
        private void LoadFileList()
        {
            try
            {
                string[] files = Directory.Exists(logDir)
                    ? Directory.GetFiles(logDir, "guard-*.log") : new string[0];
                Array.Sort(files, delegate(string a, string b) { return string.CompareOrdinal(b, a); });
                object keep = cboFiles.SelectedItem;
                cboFiles.Items.Clear();
                foreach (string f in files) cboFiles.Items.Add(Path.GetFileName(f));
                if (cboFiles.Items.Count > 0)
                {
                    cboFiles.SelectedIndex = (keep != null && cboFiles.Items.Contains(keep)) ? cboFiles.Items.IndexOf(keep) : 0;
                }
                else
                {
                    rtb.Text = "";
                    AppendLine("暂无日志。守护进程运行后, 修复记录会自动出现在这里。", Color.DimGray);
                    lblStatus.Text = "暂无日志";
                }
            }
            catch (Exception ex) { lblStatus.Text = "读取日志目录失败: " + ex.Message; }
        }

        private string CurrentPath()
        {
            object sel = cboFiles.SelectedItem;
            return sel == null ? null : Path.Combine(logDir, sel.ToString());
        }

        // 加载/自动刷新日志 (文件没变化时 3 秒轮询零开销)
        private void LoadLog(bool force)
        {
            if (loading) return;
            string path = CurrentPath();
            if (path == null) return;
            try
            {
                DateTime stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                if (!force && stamp == lastStamp) return;
                lastStamp = stamp;

                loading = true;
                rtb.Text = "";
                AppendLine("日志: " + path, Color.DimGray);

                if (!File.Exists(path)) { AppendLine("(文件不存在)", Color.DimGray); return; }

                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                int start = 0;
                if (lines.Length > 3000)
                {
                    start = lines.Length - 3000;
                    AppendLine("(日志较长, 仅显示最后 3000 行; 完整内容请点\"打开目录\"查看)", Color.DimGray);
                }
                int shown = 0, okCnt = 0, errCnt = 0;
                for (int i = start; i < lines.Length; i++)
                {
                    string l = lines[i];
                    if (l.Length == 0) continue;
                    Color c = Color.Black;
                    if (l.IndexOf("[ok]") >= 0 || l.IndexOf(">>") >= 0) { c = Color.FromArgb(0, 128, 0); okCnt++; }
                    else if (l.IndexOf("[x]") >= 0) { c = Color.FromArgb(200, 30, 30); errCnt++; }
                    else if (l.IndexOf("[!]") >= 0 || l.IndexOf("人工") >= 0) { c = Color.FromArgb(190, 110, 0); }
                    else if (l.IndexOf("修复") >= 0 || l.IndexOf("事件:") >= 0 || l.IndexOf("巡检") >= 0) { c = Color.FromArgb(0, 90, 160); }
                    AppendLine(l, c);
                    shown++;
                }
                if (chkFollow.Checked) { rtb.SelectionStart = rtb.TextLength; rtb.ScrollToCaret(); }
                long kb = new FileInfo(path).Length / 1024;
                lblStatus.Text = string.Format("共 {0} 行 · 显示 {1} 行 · {2:N0} KB · 成功 {3} 条 · 错误 {4} 条 · 每 3 秒自动刷新",
                    lines.Length, shown, kb, okCnt, errCnt);
            }
            catch (Exception ex)
            {
                lblStatus.Text = "读取失败: " + ex.Message;
            }
            finally { loading = false; }
        }

        private void AppendLine(string s, Color c)
        {
            rtb.SelectionStart = rtb.TextLength;
            rtb.SelectionLength = 0;
            rtb.SelectionColor = c;
            rtb.AppendText(s);
            rtb.AppendText("\n");
            rtb.SelectionColor = Color.Black;
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
        internal event Action<string> LogLine;    // 每条日志 (后台线程触发)
        internal event Action StateChanged;       // 状态文件变化 (后台线程触发)

        internal bool IsBusy
        {
            get { lock (gate) { return busy; } }
        }

        private sealed class PkgState
        {
            internal int Rung;
            internal DateTime LastRepair = DateTime.MinValue;
            internal DateTime LastFailure = DateTime.MinValue;
            internal DateTime ManualUntil = DateTime.MinValue;
            internal bool NotifiedManual;
            internal string InstallLocation;   // 缓存安装目录, 供 UI 判断"运行中"
            internal string FullName;          // 包全名, 供 UI 查显示名/图标
        }

        // UI 快照 DTO
        internal sealed class PkgView
        {
            internal string Family;
            internal string InstallLocation;
            internal string FullName;
            internal int Rung;
            internal DateTime LastRepair, LastFailure, ManualUntil;
            internal bool NotifiedManual;
        }

        internal List<PkgView> GetStateSnapshot()
        {
            lock (gate)
            {
                List<PkgView> r = new List<PkgView>();
                foreach (KeyValuePair<string, PkgState> kv in states)
                {
                    PkgView v = new PkgView();
                    v.Family = kv.Key;
                    v.Rung = kv.Value.Rung;
                    v.LastRepair = kv.Value.LastRepair;
                    v.LastFailure = kv.Value.LastFailure;
                    v.ManualUntil = kv.Value.ManualUntil;
                    v.NotifiedManual = kv.Value.NotifiedManual;
                    v.InstallLocation = kv.Value.InstallLocation;
                    v.FullName = kv.Value.FullName;
                    r.Add(v);
                }
                return r;
            }
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
            // 3 秒后异步补全在册应用的 安装位置/包全名 (UI 图标/友好名依赖)
            System.Threading.Timer hyd = null;
            hyd = new System.Threading.Timer(delegate
            {
                hyd.Dispose();
                try { HydrateLocations(); } catch { }
            }, null, 3000, Timeout.Infinite);
            WriteLog("[ok] 守护启动, 事件监听 + 周期巡检已就绪");
        }

        // 一次性查询全部在册应用的位置/全名补进状态库 (UI 图标/名称依赖)
        internal void HydrateLocations()
        {
            List<string> fams;
            lock (gate) { fams = states.Keys.ToList(); }
            if (fams.Count == 0) return;
            StringBuilder fl = new StringBuilder("@('");
            fl.Append(string.Join("','", fams.ToArray()));
            fl.Append("')");
            string script =
                "$want = " + fl.ToString() + "; "
                + "Get-AppxPackage | Where-Object { $want -contains $_.PackageFamilyName } | ForEach-Object { "
                + "  \"$($_.PackageFamilyName)`t$($_.InstallLocation)`t$($_.PackageFullName)\" }";
            List<string> lines = PsLines(script, 120);
            if (lines == null) return;
            int n = 0;
            lock (gate)
            {
                foreach (string line in lines)
                {
                    string[] p = line.Trim().Split('\t');
                    if (p.Length < 3) continue;
                    PkgState st;
                    if (!states.TryGetValue(p[0], out st)) continue;
                    if (st.InstallLocation == null && p[1].Length > 0) { st.InstallLocation = p[1]; n++; }
                    if (st.FullName == null && p[2].Length > 0) st.FullName = p[2];
                }
            }
            if (n > 0)
            {
                WriteLog("[ok] 已补全 " + n + " 个应用的安装位置 (供界面显示图标/名称)");
                SaveState();
            }
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
                PkgState stInfo = GetState(fam); stInfo.InstallLocation = loc; stInfo.FullName = fullname; // 缓存给 UI 用
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
                    SaveState(); // 持久化 FullName/InstallLocation 供 UI 使用
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
                        if (p.Length > 5 && p[5].Length > 0) st.InstallLocation = p[5];
                        if (p.Length > 6 && p[6].Length > 0) st.FullName = p[6];
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
                            + kv.Value.ManualUntil.Ticks + "\t" + (kv.Value.NotifiedManual ? "1" : "0")
                            + "\t" + (kv.Value.InstallLocation == null ? "" : kv.Value.InstallLocation)
                            + "\t" + (kv.Value.FullName == null ? "" : kv.Value.FullName));
                    File.WriteAllText(StatePath, sb.ToString(), Utf8NoBom);
                }
                catch { }
            }
            Action h = StateChanged;
            if (h != null) { try { h(); } catch { } }
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
            Action<string> hl = LogLine;
            if (hl != null) { try { hl(line); } catch { } }
        }

        private static readonly Encoding Utf8Bom = new UTF8Encoding(true);
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    }
}
