using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Timer = System.Windows.Forms.Timer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        bool background = args.Any(arg => arg == "--background");
        bool firstInstance;
        using (var activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DiskRootWriter.Activate"))
        using (var instance = new Mutex(true, @"Local\DiskRootWriter.Instance", out firstInstance))
        {
            if (!firstInstance)
            {
                if (!background)
                    activation.Set();
                return;
            }

            try
            {
                SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var app = new WriterApplication(background, activation))
                    Application.Run(app);
            }
            finally
            {
                instance.ReleaseMutex();
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();
}

internal sealed class Options
{
    internal const int MaxMinutes = 9999;
    internal const int DefaultDeletionDelay = 3;
    internal const int MaxDeletionDelay = 3600;
    internal readonly string VolumeId;
    internal readonly string Root;
    internal readonly int IntervalSeconds;
    internal readonly bool DeleteAfterWrite;
    internal readonly int DeletionDelaySeconds;

    internal Options(string volumeId, string root, int intervalSeconds, bool deleteAfterWrite, int deletionDelaySeconds)
    {
        VolumeId = volumeId;
        Root = root;
        IntervalSeconds = intervalSeconds;
        DeleteAfterWrite = deleteAfterWrite;
        DeletionDelaySeconds = deletionDelaySeconds;
    }

    internal static Options Parse(string value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        string[] fields = value.Split('|');
        int seconds;
        int deletionDelay = DefaultDeletionDelay;
        Guid id;
        if ((fields.Length != 4 && fields.Length != 5) || !Guid.TryParseExact(fields[0], "D", out id) ||
            fields[1].Length != 3 || fields[1][0] < 'A' || fields[1][0] > 'Z' ||
            fields[1][1] != ':' || fields[1][2] != '\\' ||
            !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out seconds) ||
            seconds < 1 || seconds > MaxMinutes * 60 + 59 ||
            (fields[3] != "0" && fields[3] != "1") ||
            (fields.Length == 5 &&
                (!int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out deletionDelay) ||
                    deletionDelay < 1 || deletionDelay > MaxDeletionDelay)))
            return null;

        return new Options(id.ToString("D"), fields[1], seconds, fields[3] == "1", deletionDelay);
    }

    public override string ToString()
    {
        return string.Join("|", VolumeId, Root, IntervalSeconds.ToString(CultureInfo.InvariantCulture),
            DeleteAfterWrite ? "1" : "0", DeletionDelaySeconds.ToString(CultureInfo.InvariantCulture));
    }
}

internal static class Preferences
{
    private const string SettingsPath = @"Software\DiskRootWriter";
    private const string StartupPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupName = "DiskRootWriter";

    internal static Options Load()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(SettingsPath))
            return Options.Parse(key == null ? null : key.GetValue("Settings") as string);
    }

    internal static void Save(Options options)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(SettingsPath))
            key.SetValue("Settings", options.ToString(), RegistryValueKind.String);
    }

    internal static bool StartsWithWindows()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(StartupPath))
            return key != null && key.GetValue(StartupName) is string;
    }

    internal static void SetStartup(bool enabled)
    {
        if (enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(StartupPath))
                key.SetValue(StartupName, "\"" + Application.ExecutablePath + "\" --background", RegistryValueKind.String);
        }
        else
        {
            using (var key = Registry.CurrentUser.OpenSubKey(StartupPath, true))
                if (key != null)
                    key.DeleteValue(StartupName, false);
        }
    }
}

internal sealed class Volume
{
    internal readonly string Id;
    internal readonly string Root;
    private readonly string displayName;

    internal Volume(string id, string root, string label)
    {
        Id = id;
        Root = root;
        displayName = string.IsNullOrEmpty(label) ? root : root + "  " + label;
    }

    public override string ToString()
    {
        return displayName;
    }

    internal static List<Volume> FindAvailable()
    {
        var volumes = new List<Volume>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable)
                    continue;

                string id = ReadId(drive.Name);
                if (id != null && drive.IsReady)
                    volumes.Add(new Volume(id, drive.Name, drive.VolumeLabel));
            }
            catch (IOException)
            {
                // A removable drive can disconnect during enumeration.
            }
            catch (UnauthorizedAccessException)
            {
                // Keep the rest of the drive list available.
            }
        }
        return volumes;
    }

    internal static string ResolveRoot(Options options)
    {
        if (ReadId(options.Root) == options.VolumeId)
            return options.Root;

        foreach (DriveInfo drive in DriveInfo.GetDrives())
            if (drive.Name != options.Root && ReadId(drive.Name) == options.VolumeId)
                return drive.Name;

        throw new IOException("目标磁盘当前未连接。");
    }

    internal static string NativeRoot(Options options)
    {
        return @"\\?\Volume{" + options.VolumeId + @"}\";
    }

    private static string ReadId(string root)
    {
        var name = new StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPoint(root, name, name.Capacity))
            return null;

        string path = name.ToString();
        int start = path.IndexOf('{');
        int end = path.IndexOf('}');
        Guid id;
        return start >= 0 && end > start && Guid.TryParse(path.Substring(start + 1, end - start - 1), out id)
            ? id.ToString("D")
            : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, int capacity);
}

internal static class DiskWriter
{
    internal const string PermanentName = "DiskRootWriter.empty";
    private const uint GenericWrite = 0x40000000;
    private const uint Delete = 0x00010000;
    private const uint CreateNew = 1;
    private const uint OpenAlways = 4;
    private const uint NormalAttributes = 0x00000080;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint DeleteOnClose = 0x04000000;
    private const uint WriteThrough = 0x80000000;

    internal static void WritePermanent(string root, out string filename)
    {
        filename = PermanentName;
        using (SafeFileHandle file = OpenFile(root + filename, false))
        using (var stream = new FileStream(file, FileAccess.Write))
        {
            if (stream.Length != 0)
                throw new IOException(filename + " 已存在且含有内容，无法写入。");

            long writeTime = DateTime.UtcNow.ToFileTimeUtc();
            if (!SetFileTime(file, IntPtr.Zero, IntPtr.Zero, ref writeTime))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            stream.Flush(true);
        }
    }

    internal static Task WriteTemporary(string root, int seconds, CancellationToken exit, out string filename)
    {
        filename = "DiskRootWriter-" + Guid.NewGuid().ToString("N") + ".empty";
        SafeFileHandle file = OpenFile(root + filename, true);
        if (FlushFileBuffers(file))
            return DeleteAfterDelay(file, seconds, exit);

        int error = Marshal.GetLastWin32Error();
        file.Dispose();
        throw new Win32Exception(error);
    }

    private static async Task DeleteAfterDelay(SafeFileHandle file, int seconds, CancellationToken exit)
    {
        using (file)
        {
            try
            {
                await Task.Delay(seconds * 1000, exit).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Closing the handle removes the temporary file when the app exits.
            }
        }
    }

    private static SafeFileHandle OpenFile(string path, bool temporary)
    {
        uint access = temporary ? GenericWrite | Delete : GenericWrite;
        uint share = temporary ? (uint)(FileShare.Read | FileShare.Delete) : (uint)FileShare.Read;
        uint disposition = temporary ? CreateNew : OpenAlways;
        uint flags = NormalAttributes | WriteThrough | OpenReparsePoint;
        if (temporary)
            flags |= DeleteOnClose;

        SafeFileHandle file = CreateFile(path, access, share, IntPtr.Zero, disposition, flags, IntPtr.Zero);
        if (file.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            file.Dispose();
            throw new Win32Exception(error);
        }
        return file;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileTime(SafeFileHandle file, IntPtr created, IntPtr accessed, ref long written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(SafeFileHandle file);
}

internal sealed class WriterApplication : ApplicationContext
{
    private readonly SettingsWindow window;
    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem pauseMenu;
    private readonly Timer timer = new Timer();
    private readonly CancellationTokenSource exitCleanup = new CancellationTokenSource();
    private readonly RegisteredWaitHandle activation;
    private Options saved;
    private bool running;
    private bool creating;
    private bool quitting;
    private bool quitReady;
    private int pendingWrites;
    private int generation;

    internal WriterApplication(bool background, EventWaitHandle activationEvent)
    {
        window = new SettingsWindow();
        window.SaveButton.Click += (sender, e) => SaveSettings(false);
        window.StartButton.Click += (sender, e) => SaveSettings(true);
        window.PauseButton.Click += (sender, e) => Pause();
        window.RefreshButton.Click += (sender, e) => RefreshDrives();
        window.FormClosing += (sender, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                window.Hide();
            }
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开设置", null, (sender, e) => ShowWindow());
        pauseMenu = (ToolStripMenuItem)menu.Items.Add("暂停写入", null, (sender, e) => Pause());
        menu.Items.Add("退出", null, (sender, e) => Quit());
        tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = SettingsWindow.AppTitle,
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.DoubleClick += (sender, e) => ShowWindow();
        timer.Tick += (sender, e) => Write();
        SetRunningControls();

        var windowHandle = window.Handle;
        activation = ThreadPool.RegisterWaitForSingleObject(activationEvent, (state, timedOut) =>
        {
            try
            {
                window.BeginInvoke(new Action(ShowWindow));
            }
            catch (InvalidOperationException)
            {
                // The application is already closing.
            }
        }, null, Timeout.Infinite, false);

        window.BeginInvoke(new Action(() => Initialize(background)));
    }

    private void Initialize(bool background)
    {
        try
        {
            saved = Preferences.Load();
            window.StartAtLogin.Checked = Preferences.StartsWithWindows();
            if (saved != null)
            {
                window.Minutes.Value = saved.IntervalSeconds / 60;
                window.Seconds.Value = saved.IntervalSeconds % 60;
                window.DeleteFiles.Checked = saved.DeleteAfterWrite;
                window.DeletionDelay.Value = saved.DeletionDelaySeconds;
            }
        }
        catch (Exception error)
        {
            window.Status("无法读取设置。 " + error.Message, true);
        }

        if (background && saved != null && window.StartAtLogin.Checked)
            Start();
        else
            ShowWindow();
    }

    private void ShowWindow()
    {
        if (quitting)
            return;

        if (!window.Visible)
        {
            RefreshDrives();
            window.Show();
        }
        if (window.WindowState == FormWindowState.Minimized)
            window.WindowState = FormWindowState.Normal;
        window.Activate();
    }

    private void RefreshDrives()
    {
        Volume selected = window.Drive.SelectedItem as Volume;
        string id = selected != null ? selected.Id : saved == null ? null : saved.VolumeId;
        string root = selected != null ? selected.Root : saved == null ? null : saved.Root;
        List<Volume> drives;
        try
        {
            drives = Volume.FindAvailable();
        }
        catch (Exception error)
        {
            window.Status("无法刷新磁盘列表。 " + error.Message, true);
            return;
        }

        Volume match = drives.Find(drive => drive.Id == id);
        if (match == null && id != null)
        {
            match = new Volume(id, root, "（未连接）");
            drives.Add(match);
        }

        window.Drive.BeginUpdate();
        window.Drive.Items.Clear();
        window.Drive.Items.AddRange(drives.ToArray());
        window.Drive.SelectedItem = match;
        window.Drive.EndUpdate();
    }

    private void SaveSettings(bool start)
    {
        if (quitting)
            return;

        var target = window.Drive.SelectedItem as Volume;
        if (target == null)
        {
            window.Status("请先选择要写入的磁盘。", true);
            window.Drive.Focus();
            return;
        }

        int seconds = (int)window.Minutes.Value * 60 + (int)window.Seconds.Value;
        if (seconds < 1)
        {
            window.Status("写入间隔至少为 1 秒。", true);
            window.Seconds.Focus();
            return;
        }

        var options = new Options(target.Id, target.Root, seconds, window.DeleteFiles.Checked,
            (int)window.DeletionDelay.Value);
        try
        {
            Preferences.Save(options);
        }
        catch (Exception error)
        {
            window.Status("无法保存设置。 " + error.Message, true);
            return;
        }

        try
        {
            Preferences.SetStartup(window.StartAtLogin.Checked);
        }
        catch (Exception error)
        {
            MessageBox.Show(window, "写入设置已保存，但无法更改开机自启。\r\n" + error.Message,
                SettingsWindow.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        bool changed = saved == null || options.ToString() != saved.ToString();
        saved = options;
        if (start || (running && changed))
            Start();
        else if (!running)
            window.Status("设置已保存。点击“开始写入”即可运行。", false);
    }

    private void Start()
    {
        generation++;
        running = true;
        timer.Stop();
        SetRunningControls();
        window.Status("正在写入…", false);
        Write();
    }

    private void Pause()
    {
        running = false;
        generation++;
        timer.Stop();
        SetRunningControls();
        window.Status("已暂停写入。", false);
    }

    private void SetRunningControls()
    {
        window.StartButton.Enabled = !running && !quitting;
        window.PauseButton.Enabled = running && !quitting;
        pauseMenu.Enabled = window.PauseButton.Enabled;
        window.SaveButton.Enabled = !quitting;
    }

    private async void Write()
    {
        if (!running || creating || quitting)
            return;

        // Only creation is serialized. Each temporary file has its own lifetime.
        timer.Stop();
        creating = true;
        pendingWrites++;
        var elapsed = Stopwatch.StartNew();
        int activeGeneration = generation;
        Options options = saved;
        string path = null;
        Task retention = null;
        string message;
        bool failed = false;
        try
        {
            await Task.Run(() =>
            {
                string root = Volume.ResolveRoot(options);
                string filename;
                if (options.DeleteAfterWrite)
                {
                    retention = DiskWriter.WriteTemporary(Volume.NativeRoot(options), options.DeletionDelaySeconds,
                        exitCleanup.Token, out filename);
                }
                else
                {
                    DiskWriter.WritePermanent(Volume.NativeRoot(options), out filename);
                }
                path = root + filename;
            });
            message = DateTime.Now.ToString("HH:mm:ss") + "  已写入 " + path;
        }
        catch (Exception error)
        {
            message = "写入失败。 " + error.Message;
            failed = true;
        }

        creating = false;
        if (running && !quitting)
        {
            if (activeGeneration == generation)
            {
                window.Status(message, failed);
                timer.Interval = (int)Math.Max(1, options.IntervalSeconds * 1000L - elapsed.ElapsedMilliseconds);
                timer.Start();
            }
            else
            {
                Write();
            }
        }

        try
        {
            if (retention != null)
                await retention;
        }
        finally
        {
            pendingWrites--;
            if (quitReady && pendingWrites == 0)
                ExitThread();
        }
    }

    private async void Quit()
    {
        if (quitting)
            return;

        quitting = true;
        running = false;
        timer.Stop();
        SetRunningControls();
        if (pendingWrites != 0)
        {
            window.Status("正在清理临时文件…", false);
            await Task.Run(() => exitCleanup.Cancel());
        }
        quitReady = true;
        if (pendingWrites == 0)
            ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            activation.Unregister(null);
            exitCleanup.Dispose();
            timer.Dispose();
            tray.Visible = false;
            tray.ContextMenuStrip.Dispose();
            tray.Dispose();
            window.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class SettingsWindow : Form
{
    internal const string AppTitle = "磁盘定时写入";
    internal readonly ComboBox Drive = new ComboBox();
    internal readonly Button RefreshButton = new Button();
    internal readonly NumericUpDown Minutes = new NumericUpDown();
    internal readonly NumericUpDown Seconds = new NumericUpDown();
    internal readonly CheckBox DeleteFiles = new CheckBox();
    internal readonly NumericUpDown DeletionDelay = new NumericUpDown();
    internal readonly CheckBox StartAtLogin = new CheckBox();
    internal readonly Button SaveButton = new Button();
    internal readonly Button StartButton = new Button();
    internal readonly Button PauseButton = new Button();
    private readonly Label status = new Label();

    internal SettingsWindow()
    {
        SuspendLayout();
        Text = AppTitle;
        Icon = SystemIcons.Application;
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(500, 411);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(248, 250, 252);
        ForeColor = Color.FromArgb(24, 37, 49);
        Padding = new Padding(24, 16, 24, 14);

        var layout = new TableLayoutPanel { ColumnCount = 1, RowCount = 10, Dock = DockStyle.Fill };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in new[] { 43, 23, 39, 23, 38, 32, 34, 50, 63, 31 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        Controls.Add(layout);

        Label title = MakeLabel(AppTitle);
        title.Font = new Font(Font.FontFamily, 17F, FontStyle.Bold);
        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(MakeLabel("目标磁盘"), 0, 1);

        var driveRow = new TableLayoutPanel
        {
            ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill, Margin = Padding.Empty
        };
        driveRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        driveRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 81));
        Drive.DropDownStyle = ComboBoxStyle.DropDownList;
        Drive.AccessibleName = "目标磁盘";
        Drive.Dock = DockStyle.Fill;
        Drive.Margin = new Padding(0, 5, 10, 0);
        ConfigureButton(RefreshButton, "刷新", 81);
        RefreshButton.Margin = new Padding(0, 2, 0, 0);
        driveRow.Controls.Add(Drive, 0, 0);
        driveRow.Controls.Add(RefreshButton, 1, 0);
        layout.Controls.Add(driveRow, 0, 2);

        layout.Controls.Add(MakeLabel("写入间隔"), 0, 3);
        var interval = MakeFlow();
        ConfigureNumber(Minutes, "分钟", Options.MaxMinutes, 1);
        ConfigureNumber(Seconds, "秒", 59, 0);
        interval.Controls.Add(Minutes);
        interval.Controls.Add(MakeUnit("分"));
        interval.Controls.Add(Seconds);
        interval.Controls.Add(MakeUnit("秒"));
        layout.Controls.Add(interval, 0, 4);

        var deletion = MakeFlow();
        DeleteFiles.Text = "写入后删除文件";
        DeleteFiles.AutoSize = true;
        DeleteFiles.Height = 28;
        DeleteFiles.Margin = new Padding(0, 6, 20, 0);
        DeleteFiles.CheckedChanged += (sender, e) => DeletionDelay.Enabled = DeleteFiles.Checked;
        ConfigureNumber(DeletionDelay, "删除前等待秒数", Options.MaxDeletionDelay, Options.DefaultDeletionDelay);
        DeletionDelay.Minimum = 1;
        DeletionDelay.Enabled = false;
        var wait = MakeUnit("等待");
        wait.Width = 38;
        wait.Margin = new Padding(0, 2, 5, 0);
        deletion.Controls.Add(DeleteFiles);
        deletion.Controls.Add(wait);
        deletion.Controls.Add(DeletionDelay);
        deletion.Controls.Add(MakeUnit("秒"));

        StartAtLogin.Text = "开机自启（登录 Windows 后开始写入）";
        StartAtLogin.Dock = DockStyle.Fill;
        StartAtLogin.Margin = Padding.Empty;
        layout.Controls.Add(deletion, 0, 5);
        layout.Controls.Add(StartAtLogin, 0, 6);

        var buttons = MakeFlow();
        buttons.Padding = new Padding(0, 5, 0, 0);
        ConfigureButton(StartButton, "开始写入", 104);
        StartButton.BackColor = Color.FromArgb(0, 95, 105);
        StartButton.ForeColor = Color.White;
        StartButton.FlatStyle = FlatStyle.Flat;
        StartButton.FlatAppearance.BorderSize = 0;
        ConfigureButton(PauseButton, "暂停写入", 104);
        ConfigureButton(SaveButton, "保存设置", 104);
        buttons.Controls.Add(StartButton);
        buttons.Controls.Add(PauseButton);
        buttons.Controls.Add(SaveButton);
        layout.Controls.Add(buttons, 0, 7);

        status.Dock = DockStyle.Fill;
        status.Padding = new Padding(11, 5, 11, 5);
        status.TextAlign = ContentAlignment.MiddleLeft;
        status.Margin = new Padding(0, 0, 0, 3);
        Status("等待开始。", false);
        layout.Controls.Add(status, 0, 8);

        Label hint = MakeLabel("关闭窗口后继续在系统托盘运行。");
        hint.ForeColor = Color.FromArgb(73, 89, 104);
        layout.Controls.Add(hint, 0, 9);
        ResumeLayout();
    }

    internal void Status(string text, bool error)
    {
        status.Text = text;
        status.BackColor = error ? Color.FromArgb(255, 239, 236) : Color.FromArgb(234, 242, 246);
        status.ForeColor = error ? Color.FromArgb(136, 38, 31) : Color.FromArgb(28, 62, 80);
    }

    private static Label MakeLabel(string text)
    {
        return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
    }

    private static FlowLayoutPanel MakeFlow()
    {
        return new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
    }

    private static Label MakeUnit(string text)
    {
        return new Label { Text = text, Width = 23, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 2, 12, 0) };
    }

    private static void ConfigureNumber(NumericUpDown field, string name, int max, int initial)
    {
        field.AccessibleName = name;
        field.Width = 82;
        field.Maximum = max;
        field.Value = initial;
        field.TextAlign = HorizontalAlignment.Right;
        field.Margin = new Padding(0, 4, 7, 0);
    }

    private static void ConfigureButton(Button button, string text, int width)
    {
        button.Text = text;
        button.Size = new Size(width, 33);
        button.Margin = new Padding(0, 0, 9, 0);
    }
}
