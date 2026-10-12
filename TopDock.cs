using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

class Item
{
    public string Path, Name, Cmd, Args, Key, Id;
    public int Kind; // 0=文件 1=系统项 2=自定义命令
    public Bitmap Icon;
    public Rectangle Rect;
}

class Dock : Form
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr p, IntPtr min, IntPtr max);

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)]
    struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [DllImport("user32.dll")]
    static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO fi, uint size, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    static extern IntPtr SHGetFileInfo(IntPtr pidl, uint attr, ref SHFILEINFO fi, uint size, uint flags);
    [DllImport("shell32.dll")]
    static extern int SHGetSpecialFolderLocation(IntPtr hwnd, int csidl, out IntPtr pidl);
    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint n);

    const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string APP = "TopDock";

    static readonly string[][] SYS = new string[][] {
        new string[] { "pc",  "此电脑",   "explorer.exe", "shell:MyComputerFolder",   "17" },
        new string[] { "bin", "回收站",   "explorer.exe", "shell:RecycleBinFolder",   "10" },
        new string[] { "cp",  "控制面板", "control.exe",  "",                         "3"  },
        new string[] { "set", "设置",     "explorer.exe", "ms-settings:",             "-1" },
        new string[] { "net", "网络",     "explorer.exe", "shell:NetworkPlacesFolder","18" }
    };

    int ICON, PAD, GAP, BW, PEEK, scrollY, contentH;
    int downIdx = -1, dropIdx = -1;
    Point downPt;
    bool reorder;
    string baseDir, dir, cfgPath, cmdPath, orderPath;
    List<Item> items = new List<Item>();
    int hover = -1;
    volatile bool dirty = true;
    bool shown = true, menuOpen, dragging;
    System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    ToolTip tip = new ToolTip();
    FileSystemWatcher fsw, fsw2;

    // 设置
    int bgPct = 60;      // 背景不透明度（只影响背景，不影响图标）
    int iconPx = 32;     // 图标大小（96dpi 下的像素）
    int vpos = 0;        // 栏在左边缘的位置：0靠上 1居中 2靠下
    int gapPx = 8;       // 图标间距
    bool pinned = false;
    List<string> sysKeys = new List<string>(new string[] { "pc", "bin", "cp" });

    [STAThread]
    static void Main()
    {
        SetProcessDPIAware();
        bool created;
        using (var m = new Mutex(true, "TopDock_single_instance", out created))
        {
            if (!created) return;
            Application.EnableVisualStyles();
            Application.Run(new Dock());
        }
    }

    float dpi = 96f;
    int S(int v) { return (int)Math.Round(v * dpi / 96f); }

    void Metrics()
    {
        ICON = S(iconPx);
        BW = ICON + S(16);
        PAD = S(8);
        GAP = S(gapPx);
    }

    public Dock()
    {
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiX;
        PEEK = 0;
        baseDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TopDock");
        dir = System.IO.Path.Combine(baseDir, "items");
        cfgPath = System.IO.Path.Combine(baseDir, "config.ini");
        cmdPath = System.IO.Path.Combine(baseDir, "commands.txt");
        orderPath = System.IO.Path.Combine(baseDir, "order.txt");
        try { Directory.CreateDirectory(baseDir); } catch { }
        LoadCfg();
        try { Directory.CreateDirectory(dir); } catch { }
        Metrics();

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AllowDrop = true;

        SetupWatch();
        FileSystemEventHandler h = delegate { dirty = true; };
        fsw2 = new FileSystemWatcher(baseDir, "commands.txt");
        fsw2.Changed += h; fsw2.Created += h;
        fsw2.EnableRaisingEvents = true;

        Reload();
        timer.Interval = 120;
        timer.Tick += delegate { Tick(); };
        timer.Start();
    }

    void SetupWatch()
    {
        try
        {
            if (fsw != null) { fsw.EnableRaisingEvents = false; fsw.Dispose(); }
            FileSystemEventHandler h = delegate { dirty = true; };
            fsw = new FileSystemWatcher(dir);
            fsw.Created += h; fsw.Deleted += h; fsw.Changed += h;
            fsw.Renamed += delegate { dirty = true; };
            fsw.EnableRaisingEvents = true;
        }
        catch { }
    }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x08000000 | 0x80000; // TOOLWINDOW | NOACTIVATE | LAYERED
            return cp;
        }
    }
    protected override void OnShown(EventArgs e) { base.OnShown(e); Redraw(); }

    // ---------- 设置 ----------
    void LoadCfg()
    {
        try
        {
            if (!File.Exists(cfgPath)) return;
            foreach (var line in File.ReadAllLines(cfgPath))
            {
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                int n;
                if (k == "bg" && int.TryParse(v, out n)) bgPct = Math.Max(0, Math.Min(100, n));
                else if (k == "size" && int.TryParse(v, out n)) iconPx = Math.Max(16, Math.Min(64, n));
                else if (k == "vpos" && int.TryParse(v, out n)) vpos = Math.Max(0, Math.Min(2, n));
                else if (k == "gap" && int.TryParse(v, out n)) gapPx = Math.Max(0, Math.Min(60, n));
                else if (k == "pinned") pinned = (v == "1");
                else if (k == "dir" && v.Length > 0) dir = v;
                else if (k == "sys")
                {
                    sysKeys.Clear();
                    foreach (var s in v.Split(',')) if (s.Trim().Length > 0) sysKeys.Add(s.Trim());
                }
            }
        }
        catch { }
    }

    void SaveCfg()
    {
        try
        {
            File.WriteAllLines(cfgPath, new string[] {
                "bg=" + bgPct, "size=" + iconPx, "vpos=" + vpos, "gap=" + gapPx,
                "pinned=" + (pinned ? "1" : "0"), "dir=" + dir,
                "sys=" + string.Join(",", sysKeys.ToArray()) },
                new System.Text.UTF8Encoding(true));
        }
        catch { }
    }

    // ---------- 图标 ----------
    Bitmap IconToBmp(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return null;
        try { using (var ic = Icon.FromHandle(hIcon)) return ic.ToBitmap(); }
        catch { return null; }
        finally { DestroyIcon(hIcon); }
    }

    Bitmap GetIcon(string path)
    {
        if (path == null) return null;
        var fi = new SHFILEINFO();
        IntPtr r = SHGetFileInfo(path, 0, ref fi, (uint)Marshal.SizeOf(fi), 0x100);
        if (r == IntPtr.Zero) return null;
        return IconToBmp(fi.hIcon);
    }

    Bitmap ExtractBmp(string file, int idx)
    {
        IntPtr[] big = new IntPtr[1];
        try { if (ExtractIconEx(file, idx, big, null, 1) < 1) return null; }
        catch { return null; }
        return IconToBmp(big[0]);
    }

    // 读取快捷方式的真实图标，避免带上"快捷方式小箭头"
    Bitmap LinkIcon(string lnk)
    {
        object sh = null, sc = null;
        try
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            sh = Activator.CreateInstance(t);
            sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
            Type lt = sc.GetType();
            string target = lt.InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null) as string;
            string loc = lt.InvokeMember("IconLocation", BindingFlags.GetProperty, null, sc, null) as string;
            Bitmap b = null;
            if (!string.IsNullOrEmpty(loc))
            {
                int idx = 0; string f = loc;
                int c = loc.LastIndexOf(',');
                if (c > 0) { int.TryParse(loc.Substring(c + 1), out idx); f = loc.Substring(0, c); }
                f = Environment.ExpandEnvironmentVariables(f.Trim().Trim('"'));
                if (File.Exists(f)) b = ExtractBmp(f, idx);
            }
            if (b == null && !string.IsNullOrEmpty(target) && (File.Exists(target) || Directory.Exists(target)))
                b = GetIcon(target);
            return b;
        }
        catch { return null; }
        finally
        {
            if (sc != null) Marshal.ReleaseComObject(sc);
            if (sh != null) Marshal.ReleaseComObject(sh);
        }
    }

    Bitmap SysIcon(int csidl)
    {
        IntPtr pidl;
        if (SHGetSpecialFolderLocation(IntPtr.Zero, csidl, out pidl) != 0 || pidl == IntPtr.Zero) return null;
        try
        {
            var fi = new SHFILEINFO();
            IntPtr r = SHGetFileInfo(pidl, 0, ref fi, (uint)Marshal.SizeOf(fi), 0x108);
            if (r == IntPtr.Zero) return null;
            return IconToBmp(fi.hIcon);
        }
        finally { Marshal.FreeCoTaskMem(pidl); }
    }

    static string Resolve(string cmd)
    {
        if (string.IsNullOrEmpty(cmd)) return null;
        if (File.Exists(cmd) || Directory.Exists(cmd)) return cmd;
        string[] dirs = { Environment.SystemDirectory, Environment.GetEnvironmentVariable("WINDIR") };
        foreach (var d in dirs)
        {
            if (d == null) continue;
            string p = System.IO.Path.Combine(d, cmd);
            if (File.Exists(p)) return p;
            if (File.Exists(p + ".exe")) return p + ".exe";
        }
        return null;
    }

    // ---------- 加载 ----------
    void EnsureCmdFile()
    {
        if (File.Exists(cmdPath)) return;
        File.WriteAllText(cmdPath,
            "# 每行一个：名称|命令|参数    （以 # 开头的行会被忽略）\r\n" +
            "# 例子（去掉前面的#即可生效）：\r\n" +
            "# 任务管理器|taskmgr.exe|\r\n" +
            "# 计算器|calc.exe|\r\n" +
            "# 命令提示符|cmd.exe|\r\n" +
            "# 设备管理器|devmgmt.msc|\r\n" +
            "# 服务|services.msc|\r\n" +
            "# 网络连接|ncpa.cpl|\r\n" +
            "# 程序和功能|appwiz.cpl|\r\n" +
            "# 下载文件夹|explorer.exe|shell:Downloads\r\n" +
            "# 我的项目|explorer.exe|D:\\project\r\n",
            new System.Text.UTF8Encoding(true));
    }

    void Reload()
    {
        dirty = false;
        Metrics();
        foreach (var it in items) if (it.Icon != null) it.Icon.Dispose();
        items.Clear();

        foreach (var key in sysKeys)
        {
            foreach (var d in SYS)
            {
                if (d[0] != key) continue;
                var it = new Item();
                it.Kind = 1; it.Key = key; it.Name = d[1]; it.Cmd = d[2]; it.Args = d[3];
                it.Id = "sys:" + key;
                int cs = int.Parse(d[4]);
                Bitmap b = null;
                if (key == "set")
                    b = GetIcon(System.IO.Path.Combine(Environment.GetEnvironmentVariable("WINDIR"), @"ImmersiveControlPanel\SystemSettings.exe"));
                if (b == null && cs >= 0) b = SysIcon(cs);
                if (b == null) b = GetIcon(Resolve(d[2]));
                it.Icon = b;
                items.Add(it);
            }
        }

        try
        {
            if (File.Exists(cmdPath))
            {
                foreach (var raw in File.ReadAllLines(cmdPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    string[] p = line.Split('|');
                    if (p.Length < 2) continue;
                    var it = new Item();
                    it.Kind = 2; it.Name = p[0].Trim(); it.Cmd = p[1].Trim();
                    it.Args = p.Length > 2 ? p[2].Trim() : "";
                    it.Id = "cmd:" + it.Name + "|" + it.Cmd;
                    Bitmap b = GetIcon(Resolve(it.Cmd));
                    if (b == null && it.Args.Length > 0 && Directory.Exists(it.Args)) b = GetIcon(it.Args);
                    it.Icon = b;
                    items.Add(it);
                }
            }
        }
        catch { }

        try
        {
            var list = new List<string>(Directory.GetFileSystemEntries(dir));
            list.Sort(StringComparer.CurrentCultureIgnoreCase);
            foreach (var p in list)
            {
                string n = System.IO.Path.GetFileName(p);
                if (n.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var it = new Item();
                it.Kind = 0; it.Path = p;
                it.Id = "file:" + n;
                it.Name = Directory.Exists(p) ? n : System.IO.Path.GetFileNameWithoutExtension(p);
                Bitmap b = null;
                if (p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) b = LinkIcon(p);
                if (b == null) b = GetIcon(p);
                it.Icon = b;
                items.Add(it);
            }
        }
        catch { }

        ApplyOrder();
        Arrange();
        Trim();
    }

    // ---------- 排序 ----------
    void ApplyOrder()
    {
        try
        {
            if (!File.Exists(orderPath)) return;
            var res = new List<Item>();
            foreach (var line in File.ReadAllLines(orderPath))
            {
                string id = line.Trim();
                if (id.Length == 0) continue;
                for (int k = 0; k < items.Count; k++)
                {
                    if (items[k].Id == id) { res.Add(items[k]); items.RemoveAt(k); break; }
                }
            }
            res.AddRange(items);
            items = res;
        }
        catch { }
    }

    void SaveOrder()
    {
        try
        {
            var ids = new List<string>();
            foreach (var it in items) ids.Add(it.Id);
            File.WriteAllLines(orderPath, ids.ToArray(), new System.Text.UTF8Encoding(true));
        }
        catch { }
    }

    // to 表示"插入到原列表的第几个位置之前"
    void MoveItem(int from, int to)
    {
        if (from < 0 || from >= items.Count) return;
        if (to > from) to--;
        if (to < 0) to = 0;
        if (to > items.Count - 1) to = items.Count - 1;
        if (to == from) return;
        var it = items[from];
        items.RemoveAt(from);
        items.Insert(to, it);
        SaveOrder();
        Arrange();
    }

    // ---------- 布局（竖排） ----------
    void Arrange()
    {
        int y = PAD;
        foreach (var it in items)
        {
            it.Rect = new Rectangle((BW - ICON) / 2, y, ICON, ICON);
            y += ICON + GAP;
        }
        contentH = items.Count == 0 ? S(110) : y - GAP + PAD;
        Layout2();
    }

    int BarTop()
    {
        var wa = Screen.PrimaryScreen.WorkingArea;
        if (vpos == 0) return wa.Top;
        if (vpos == 1) return wa.Top + (wa.Height - Height) / 2;
        return wa.Bottom - Height;
    }

    void Layout2()
    {
        var sb = Screen.PrimaryScreen.Bounds;
        var wa = Screen.PrimaryScreen.WorkingArea;
        Width = BW;
        Height = Math.Min(contentH, wa.Height);
        Left = shown ? sb.Left : sb.Left - BW + PEEK;
        Top = BarTop();
        UpdateScroll();
        Redraw();
    }

    void UpdateScroll()
    {
        if (contentH > Height)
        {
            int minScroll = Height - contentH;
            if (scrollY < minScroll) scrollY = minScroll;
            if (scrollY > 0) scrollY = 0;
        }
        else scrollY = 0;
    }

    void Trim() { GC.Collect(); SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1)); }

    // ---------- 自动隐藏 ----------
    void Tick()
    {
        if (dirty) Reload();
        var sb = Screen.PrimaryScreen.Bounds;
        Point p = Cursor.Position;
        int margin = S(30);
        bool inY = p.Y >= Top - margin && p.Y <= Top + Height + margin;
        bool want;
        if (pinned || menuOpen || dragging || reorder) want = true;
        else if (shown) want = inY && p.X <= sb.Left + Width + 6;
        else want = inY && p.X <= sb.Left + 1;

        if (want && !shown) { shown = true; }
        else if (!want && shown) { shown = false; hover = -1; tip.Hide(this); Redraw(); }

        int ty = BarTop();
        if (Top != ty) Top = ty;
        int tx = shown ? sb.Left : sb.Left - Width + PEEK;
        if (Left != tx)
        {
            int dx = tx - Left;
            int step = Math.Max(S(4), Math.Abs(dx) / 3);
            Left += Math.Sign(dx) * Math.Min(step, Math.Abs(dx));
            timer.Interval = 16;
        }
        else
        {
            if (timer.Interval != 120) { timer.Interval = 120; if (!shown) Trim(); }
        }
    }

    // ---------- 绘制（逐像素透明：只有背景透明，图标不透明） ----------
    Rectangle R(Item it) { var r = it.Rect; r.Y += scrollY; return r; }

    static GraphicsPath BarPath(int w, int h, int r)
    {
        var p = new GraphicsPath();
        p.AddLine(0, 0, w - r, 0);
        p.AddArc(w - 2 * r, 0, 2 * r, 2 * r, 270, 90);
        p.AddLine(w, r, w, h - r);
        p.AddArc(w - 2 * r, h - 2 * r, 2 * r, 2 * r, 0, 90);
        p.AddLine(w - r, h, 0, h);
        p.CloseFigure();
        return p;
    }

    void Redraw()
    {
        if (!IsHandleCreated || Width <= 0 || Height <= 0) return;
        using (var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;

                int a = Math.Max(2, bgPct * 255 / 100); // 至少为2，保证空白处也能点到右键菜单
                using (var bgBrush = new SolidBrush(Color.FromArgb(a, 28, 30, 34)))
                using (var bgPath = BarPath(Width, Height, Math.Min(S(10), Height / 2)))
                    g.FillPath(bgBrush, bgPath);

                var sf = new StringFormat();
                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;

                if (items.Count == 0)
                {
                    using (var f = new Font("Microsoft YaHei UI", 9f))
                        g.DrawString("右键\n菜单", f, Brushes.Gainsboro, new RectangleF(0, 0, Width, Height), sf);
                }
                else
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        var it = items[i];
                        var rc = R(it);
                        if (rc.Bottom < 0 || rc.Top > Height) continue;
                        if (i == hover)
                        {
                            var hr = rc; hr.Inflate(S(4), Math.Max(2, GAP / 2));
                            using (var hiBrush = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
                            using (var gp = Round(hr, S(6))) g.FillPath(hiBrush, gp);
                        }
                        if (it.Icon != null) g.DrawImage(it.Icon, rc);
                        else
                            using (var f = new Font("Microsoft YaHei UI", 9f))
                                g.DrawString(it.Name.Length > 0 ? it.Name.Substring(0, 1) : "?", f, Brushes.White, rc, sf);
                    }

                    // 拖动排序时的插入提示线
                    if (reorder && dropIdx >= 0)
                    {
                        int sl = Math.Max(2, GAP / 2);
                        int ly = dropIdx < items.Count ? R(items[dropIdx]).Top - sl : R(items[items.Count - 1]).Bottom + sl;
                        using (var pen = new Pen(Color.FromArgb(230, 255, 255, 255), 2f))
                            g.DrawLine(pen, S(6), ly, Width - S(6), ly);
                    }
                }
                sf.Dispose();
            }
            Push(bmp);
        }
    }

    void Push(Bitmap bmp)
    {
        IntPtr scr = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(scr);
        IntPtr hb = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(mem, hb);
        try
        {
            var sz = new SIZE(); sz.cx = bmp.Width; sz.cy = bmp.Height;
            var src = new POINT();
            var bf = new BLENDFUNCTION();
            bf.BlendOp = 0; bf.BlendFlags = 0; bf.SourceConstantAlpha = 255; bf.AlphaFormat = 1;
            UpdateLayeredWindow(Handle, scr, IntPtr.Zero, ref sz, mem, ref src, 0, ref bf, 2);
        }
        finally
        {
            SelectObject(mem, old); DeleteObject(hb); DeleteDC(mem); ReleaseDC(IntPtr.Zero, scr);
        }
    }

    static GraphicsPath Round(Rectangle r, int d)
    {
        var p = new GraphicsPath();
        if (d < 2) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ---------- 鼠标 ----------
    int HitTest(Point pt)
    {
        int slack = Math.Max(2, GAP / 2);
        if (pt.X < 0 || pt.X > Width) return -1;
        for (int i = 0; i < items.Count; i++)
        {
            var r = R(items[i]);
            if (pt.Y >= r.Top - slack && pt.Y <= r.Bottom + slack) return i;
        }
        return -1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            downIdx = HitTest(e.Location);
            downPt = e.Location;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && downIdx >= 0 && !reorder &&
            (Math.Abs(e.Y - downPt.Y) > S(6) || Math.Abs(e.X - downPt.X) > S(6)))
        {
            reorder = true; tip.Hide(this); hover = downIdx;
        }
        if (reorder)
        {
            int di = 0;
            for (int k = 0; k < items.Count; k++)
            {
                var rr = R(items[k]);
                if (e.Y > rr.Top + rr.Height / 2) di = k + 1;
            }
            dropIdx = di;
            Redraw();
            base.OnMouseMove(e);
            return;
        }

        int i = HitTest(e.Location);
        if (i != hover)
        {
            hover = i; Redraw();
            if (i >= 0) tip.Show(items[i].Name, this, Width + 4, R(items[i]).Top + ICON / 4, 3000);
            else tip.Hide(this);
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (!reorder) { hover = -1; tip.Hide(this); Redraw(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (contentH > Height)
        {
            scrollY += e.Delta > 0 ? S(48) : -S(48);
            UpdateScroll(); Redraw();
        }
        base.OnMouseWheel(e);
    }

    void Launch(Item it)
    {
        try
        {
            if (it.Kind == 0) Process.Start(new ProcessStartInfo(it.Path) { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo(it.Cmd, it.Args ?? "") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, APP); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        int i = HitTest(e.Location);
        if (e.Button == MouseButtons.Left)
        {
            if (reorder)
            {
                reorder = false;
                int from = downIdx, to = dropIdx;
                downIdx = -1; dropIdx = -1; hover = -1;
                if (from >= 0 && to >= 0) MoveItem(from, to);
                Redraw();
            }
            else
            {
                if (i >= 0 && i == downIdx) Launch(items[i]);
                downIdx = -1; dropIdx = -1;
            }
        }
        else if (e.Button == MouseButtons.Right) ShowMenu(i);
        base.OnMouseUp(e);
    }

    // ---------- 菜单 ----------
    void ShowMenu(int i)
    {
        var m = new ContextMenuStrip();
        m.Opened += delegate { menuOpen = true; };
        m.Closed += delegate { menuOpen = false; };

        if (i >= 0)
        {
            var it = items[i];
            int idx = i;
            m.Items.Add("打开", null, delegate { Launch(it); });
            m.Items.Add("前移", null, delegate { MoveItem(idx, idx - 1); });
            m.Items.Add("后移", null, delegate { MoveItem(idx, idx + 2); });
            m.Items.Add("移到最前", null, delegate { MoveItem(idx, 0); });
            m.Items.Add("移到最后", null, delegate { MoveItem(idx, items.Count); });
            if (it.Kind == 0)
            {
                string path = it.Path;
                m.Items.Add("打开所在位置", null, delegate { Process.Start("explorer.exe", "/select,\"" + path + "\""); });
                m.Items.Add("移回桌面", null, delegate { MoveTo(path, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)); Reload(); });
                m.Items.Add("从栏中删除", null, delegate
                {
                    try { if (Directory.Exists(path) && !path.EndsWith(".lnk")) Directory.Delete(path, true); else File.Delete(path); } catch { }
                    Reload();
                });
            }
            else if (it.Kind == 1)
            {
                string key = it.Key;
                m.Items.Add("从栏中移除", null, delegate { sysKeys.Remove(key); SaveCfg(); Reload(); });
            }
            m.Items.Add(new ToolStripSeparator());
        }

        // 添加
        m.Items.Add("添加文件夹…", null, delegate
        {
            menuOpen = true;
            using (var fd = new FolderBrowserDialog())
            {
                fd.Description = "选择要放进栏里的文件夹";
                if (fd.ShowDialog() == DialogResult.OK) { AddPath(fd.SelectedPath); Reload(); }
            }
            menuOpen = false;
        });
        m.Items.Add("添加文件/程序…", null, delegate
        {
            menuOpen = true;
            using (var openDlg = new OpenFileDialog())
            {
                openDlg.Multiselect = true; openDlg.DereferenceLinks = false;
                if (openDlg.ShowDialog() == DialogResult.OK) { foreach (var f in openDlg.FileNames) AddPath(f); Reload(); }
            }
            menuOpen = false;
        });

        var sysMenu = new ToolStripMenuItem("系统图标");
        foreach (var d in SYS)
        {
            string key = d[0];
            var mi = new ToolStripMenuItem(d[1]) { Checked = sysKeys.Contains(key) };
            mi.Click += delegate
            {
                if (sysKeys.Contains(key)) sysKeys.Remove(key); else sysKeys.Add(key);
                SaveCfg(); Reload();
            };
            sysMenu.DropDownItems.Add(mi);
        }
        m.Items.Add(sysMenu);
        m.Items.Add("编辑自定义命令", null, delegate
        {
            try { EnsureCmdFile(); Process.Start("notepad.exe", "\"" + cmdPath + "\""); } catch { }
        });
        m.Items.Add("导入桌面图标", null, delegate { ImportDesktop(); });
        m.Items.Add(new ToolStripSeparator());

        m.Items.Add("更改收纳文件夹…", null, delegate
        {
            menuOpen = true;
            using (var fd = new FolderBrowserDialog())
            {
                fd.Description = "选择存放图标快捷方式的文件夹（当前：" + dir + "）";
                if (fd.ShowDialog() == DialogResult.OK)
                {
                    dir = fd.SelectedPath; SaveCfg(); SetupWatch(); Reload();
                }
            }
            menuOpen = false;
        });
        m.Items.Add("打开收纳文件夹", null, delegate { Process.Start("explorer.exe", "\"" + dir + "\""); });
        m.Items.Add("恢复默认排序", null, delegate { try { File.Delete(orderPath); } catch { } Reload(); });
        m.Items.Add(new ToolStripSeparator());

        var pin = new ToolStripMenuItem("固定（常显）") { Checked = pinned };
        pin.Click += delegate { pinned = !pinned; SaveCfg(); };
        m.Items.Add(pin);

        var opMenu = new ToolStripMenuItem("背景透明度");
        foreach (int v in new int[] { 0, 20, 40, 60, 80, 100 })
        {
            int vv = v;
            var mi = new ToolStripMenuItem(vv == 0 ? "全透明（仅图标）" : vv + "% 不透明") { Checked = bgPct == vv };
            mi.Click += delegate { bgPct = vv; SaveCfg(); Redraw(); };
            opMenu.DropDownItems.Add(mi);
        }
        m.Items.Add(opMenu);

        var szMenu = new ToolStripMenuItem("图标大小");
        foreach (int sv in new int[] { 24, 32, 40 })
        {
            int s2 = sv;
            var mi = new ToolStripMenuItem(s2 + " px") { Checked = iconPx == s2 };
            mi.Click += delegate { iconPx = s2; SaveCfg(); Reload(); };
            szMenu.DropDownItems.Add(mi);
        }
        m.Items.Add(szMenu);

        var vMenu = new ToolStripMenuItem("栏的位置");
        string[] vn = { "靠上", "居中", "靠下" };
        for (int k = 0; k < vn.Length; k++)
        {
            int vp = k;
            var mi = new ToolStripMenuItem(vn[k]) { Checked = vpos == vp };
            mi.Click += delegate { vpos = vp; SaveCfg(); Layout2(); };
            vMenu.DropDownItems.Add(mi);
        }
        m.Items.Add(vMenu);

        var gMenu = new ToolStripMenuItem("图标间距");
        foreach (int gv in new int[] { 0, 2, 4, 6, 8, 12, 16, 24, 32 })
        {
            int g2 = gv;
            var mi = new ToolStripMenuItem(g2 + " px") { Checked = gapPx == g2 };
            mi.Click += delegate { gapPx = g2; SaveCfg(); Reload(); };
            gMenu.DropDownItems.Add(mi);
        }
        m.Items.Add(gMenu);

        var auto = new ToolStripMenuItem("开机自启") { Checked = IsAutoStart() };
        auto.Click += delegate { SetAutoStart(!IsAutoStart()); };
        m.Items.Add(auto);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("退出", null, delegate { Application.Exit(); });
        m.Show(Cursor.Position);
    }

    static bool IsAutoStart()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RUN_KEY)) return k != null && k.GetValue(APP) != null;
    }
    static void SetAutoStart(bool on)
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RUN_KEY, true))
        {
            if (on) k.SetValue(APP, "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue(APP, false);
        }
    }

    // ---------- 导入 / 拖放 / 添加 ----------
    static void MoveTo(string src, string destDir)
    {
        string dest = System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(src));
        if (File.Exists(dest) || Directory.Exists(dest)) return;
        try
        {
            if (Directory.Exists(src)) Directory.Move(src, dest); else File.Move(src, dest);
        }
        catch { }
    }

    void ImportDesktop()
    {
        if (MessageBox.Show("把桌面上的快捷方式/文件/文件夹【移动】到收纳栏？\n（可随时右键「移回桌面」）",
            APP, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        string[] roots = {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var p in Directory.GetFileSystemEntries(root))
            {
                if (System.IO.Path.GetFileName(p).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                MoveTo(p, dir);
            }
        }
        Reload();
    }

    static void MakeLink(string lnkPath, string target)
    {
        Type t = Type.GetTypeFromProgID("WScript.Shell");
        object sh = Activator.CreateInstance(t);
        object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnkPath });
        Type lt = lnk.GetType();
        lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
        string wd = Directory.Exists(target) ? target : System.IO.Path.GetDirectoryName(target);
        lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { wd });
        lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
    }

    void AddPath(string f)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string name = System.IO.Path.GetFileName(f.TrimEnd('\\'));
            if (string.IsNullOrEmpty(name)) name = f.Replace(":", "").Replace("\\", "");
            if (f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                File.Copy(f, System.IO.Path.Combine(dir, name), false);
            else
                MakeLink(System.IO.Path.Combine(dir, name + ".lnk"), f);
        }
        catch { }
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        dragging = true;
        e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
    }
    protected override void OnDragLeave(EventArgs e) { dragging = false; }
    protected override void OnDragDrop(DragEventArgs e)
    {
        dragging = false;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (files == null) return;
        foreach (var f in files) AddPath(f);
        Reload();
    }
}
