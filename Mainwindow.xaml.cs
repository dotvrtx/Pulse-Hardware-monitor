using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace SystemInfo;

public partial class MainWindow : Window
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsMotherboardEnabled = true,
        IsStorageEnabled = true,
        IsNetworkEnabled = true,
        IsControllerEnabled = true,
        IsBatteryEnabled = true
    };

    private readonly UpdateVisitor _visitor = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<SensorRow> _rows = new();
    private readonly Dictionary<string, StackPanel> _sections = new();

    private static readonly Dictionary<string, string> SectionTitles = new()
    {
        ["os"] = "Operating system",
        ["cpu"] = "Processor",
        ["ram"] = "Memory (RAM)",
        ["board"] = "Motherboard & BIOS",
        ["gpu"] = "Graphics card",
        ["disk"] = "Disks",
        ["net"] = "Network",
        ["battery"] = "Battery",
        ["other"] = "Other sensors"
    };

    private static readonly Brush OkBrush = MakeBrush("#81C784");
    private static readonly Brush WarnBrush = MakeBrush("#FFB74D");
    private static readonly Brush BadBrush = MakeBrush("#E57373");
    private static readonly Brush TrackBrush = MakeBrush("#262626");

    private Run? _uptimeRun, _ramTotalRun, _ramFreeRun, _ramUsedRun, _pageRun;
    private bool _busy;

    private sealed record SensorRow(ISensor Sensor, TextBlock Value, TextBlock Min, TextBlock Max, Border? Fill);

    private Brush Muted => (Brush)FindResource("MutedBrush");
    private Brush Accent => (Brush)FindResource("AccentBrush");
    private Brush TextBrush => (Brush)FindResource("TextBrush");
    private Style RowStyle => (Style)FindResource("Row");

    public MainWindow()
    {
        InitializeComponent();
        _timer.Tick += OnTick;
        Loaded += async (_, _) => await InitAsync();
        Closing += (_, _) =>
        {
            _timer.Stop();
            try { _computer.Close(); } catch { }
        };
    }

    private async Task InitAsync()
    {
        try { await Task.Run(() => _computer.Open()); } catch { }

        await BuildOs();
        await BuildCpu();
        await BuildRam();
        await BuildBoard();
        await BuildGpu();
        await BuildDisks();
        BuildNetwork();
        BuildSensors();

        bool admin = IsAdmin();
        StatusText.Text = $"Refreshing every 1 s  •  {Environment.MachineName}  •  " +
                          (admin ? "administrator mode" : "NO administrator rights – some sensors will be empty");

        UpdateLive();
        _timer.Start();
    }

    private static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private async Task BuildOs()
    {
        var p = GetSection("os", expanded: true);
        var os = (await Task.Run(() => Wmi(
            "SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate FROM Win32_OperatingSystem")))
            .FirstOrDefault();

        string? display = Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", null) as string;

        AddRow(p, "Name", S(os, "Caption"));
        AddRow(p, "Version", $"{display ?? ""} (version {S(os, "Version")}, build {S(os, "BuildNumber")})".Trim());
        AddRow(p, "Architecture", S(os, "OSArchitecture"));
        AddRow(p, "Install date", ToDate(S(os, "InstallDate")));
        AddRow(p, "Computer name", Environment.MachineName);
        AddRow(p, "User", Environment.UserName);
        _uptimeRun = AddRow(p, "Uptime", "...");
    }

    private async Task BuildCpu()
    {
        var p = GetSection("cpu", expanded: true);
        var cpus = await Task.Run(() => Wmi(
            "SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, " +
            "L2CacheSize, L3CacheSize, SocketDesignation FROM Win32_Processor"));

        int i = 0;
        foreach (var c in cpus)
        {
            string sfx = cpus.Count > 1 ? $" {++i}" : "";
            AddRow(p, "Model" + sfx, S(c, "Name"));
            AddRow(p, "Manufacturer" + sfx, S(c, "Manufacturer"));
            AddRow(p, "Cores / threads" + sfx, $"{S(c, "NumberOfCores")} / {S(c, "NumberOfLogicalProcessors")}");
            AddRow(p, "Base clock" + sfx, $"{S(c, "MaxClockSpeed")} MHz");
            AddRow(p, "L2 / L3 cache" + sfx,
                $"{Kb(c, "L2CacheSize")} / {Kb(c, "L3CacheSize")}");
            AddRow(p, "Socket" + sfx, S(c, "SocketDesignation"));
        }
    }

    private async Task BuildRam()
    {
        var p = GetSection("ram");
        _ramTotalRun = AddRow(p, "Total", "...");
        _ramFreeRun = AddRow(p, "Free", "...");
        _ramUsedRun = AddRow(p, "Used", "...");
        _pageRun = AddRow(p, "Page file (free / total)", "...");

        var mods = await Task.Run(() => Wmi(
            "SELECT BankLabel, DeviceLocator, Manufacturer, PartNumber, Capacity, Speed, " +
            "ConfiguredClockSpeed, SMBIOSMemoryType FROM Win32_PhysicalMemory"));

        int n = 0;
        foreach (var m in mods)
        {
            n++;
            string type = S(m, "SMBIOSMemoryType") switch
            {
                "20" => "DDR", "21" => "DDR2", "24" => "DDR3", "26" => "DDR4", "34" => "DDR5", _ => ""
            };
            long.TryParse(S(m, "Capacity"), out long cap);
            string speed = S(m, "ConfiguredClockSpeed") != "—" ? S(m, "ConfiguredClockSpeed") : S(m, "Speed");
            AddRow(p, $"Module {n} ({S(m, "DeviceLocator")})",
                $"{FmtBytes(cap)} {type} @ {speed} MHz  —  {S(m, "Manufacturer")} {S(m, "PartNumber")}");
        }
    }

    private async Task BuildBoard()
    {
        var p = GetSection("board");
        var board = (await Task.Run(() => Wmi("SELECT Manufacturer, Product FROM Win32_BaseBoard"))).FirstOrDefault();
        var bios = (await Task.Run(() => Wmi(
            "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))).FirstOrDefault();
        var sys = (await Task.Run(() => Wmi("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))).FirstOrDefault();

        AddRow(p, "Computer", $"{S(sys, "Manufacturer")} {S(sys, "Model")}");
        AddRow(p, "Motherboard", $"{S(board, "Manufacturer")} {S(board, "Product")}");
        AddRow(p, "BIOS", $"{S(bios, "Manufacturer")} {S(bios, "SMBIOSBIOSVersion")}");
        AddRow(p, "BIOS date", ToDate(S(bios, "ReleaseDate")));
    }

    private async Task BuildGpu()
    {
        var p = GetSection("gpu");
        var gpus = await Task.Run(() => Wmi(
            "SELECT Name, DriverVersion, DriverDate, CurrentHorizontalResolution, " +
            "CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController"));

        int i = 0;
        foreach (var g in gpus)
        {
            string sfx = gpus.Count > 1 ? $" {++i}" : "";
            AddRow(p, "Model" + sfx, S(g, "Name"));
            AddRow(p, "Driver" + sfx, $"{S(g, "DriverVersion")} ({ToDate(S(g, "DriverDate"))})");
            if (S(g, "CurrentHorizontalResolution") != "—")
                AddRow(p, "Resolution" + sfx,
                    $"{S(g, "CurrentHorizontalResolution")}×{S(g, "CurrentVerticalResolution")} @ {S(g, "CurrentRefreshRate")} Hz");
        }
    }

    private async Task BuildDisks()
    {
        var p = GetSection("disk");
        var disks = await Task.Run(() => Wmi("SELECT Model, Size, InterfaceType FROM Win32_DiskDrive"));
        int i = 0;
        foreach (var d in disks)
        {
            long.TryParse(S(d, "Size"), out long size);
            AddRow(p, $"Disk {++i}", $"{S(d, "Model")}  —  {FmtBytes(size)} ({S(d, "InterfaceType")})");
        }

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            try
            {
                double usedPct = 100.0 * (drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize;
                AddRow(p, $"Partition {drive.Name} ({drive.DriveFormat})",
                    $"{FmtBytes(drive.AvailableFreeSpace)} free of {FmtBytes(drive.TotalSize)}  ({usedPct:F0}% used)");
            }
            catch { }
        }
    }

    private void BuildNetwork()
    {
        var p = GetSection("net");
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up ||
                ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            string ip = ni.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "—";
            string speed = ni.Speed > 0 ? $"{ni.Speed / 1_000_000} Mb/s" : "—";
            AddRow(p, ni.Name, $"{ip}  •  {speed}  •  {ni.Description}");
        }
    }

    private void BuildSensors()
    {
        foreach (var hw in Flatten(_computer.Hardware))
        {
            string key = hw.HardwareType switch
            {
                HardwareType.Cpu => "cpu",
                HardwareType.Memory => "ram",
                HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => "board",
                HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "gpu",
                HardwareType.Storage => "disk",
                HardwareType.Network => "net",
                _ => hw.HardwareType.ToString() == "Battery" ? "battery" : "other"
            };
            AddHardware(GetSection(key), hw);
        }
    }

    private void AddHardware(StackPanel panel, IHardware hw)
    {
        var sensors = hw.Sensors.OrderBy(s => s.SensorType).ThenBy(s => s.Index).ToList();
        if (sensors.Count == 0) return;

        panel.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 8) });
        panel.Children.Add(new TextBlock
        {
            Text = hw.Name,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = Accent,
            TextWrapping = TextWrapping.Wrap
        });

        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int c = 0; c < 3; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 90 });

        int row = 0;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Put(grid, Cell("Sensor", muted: true), row, 0);
        Put(grid, Cell("Value", muted: true, right: true), row, 2);
        Put(grid, Cell("Min", muted: true, right: true), row, 3);
        Put(grid, Cell("Max", muted: true, right: true), row, 4);
        row++;

        SensorType? current = null;
        foreach (var s in sensors)
        {
            if (s.SensorType != current)
            {
                current = s.SensorType;
                var title = Cell(TypeTitle(s.SensorType));
                title.FontWeight = FontWeights.SemiBold;
                title.Margin = new Thickness(0, 12, 0, 3);
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Put(grid, title, row, 0);
                Grid.SetColumnSpan(title, 5);
                row++;
            }

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var name = Cell(s.Name);
            name.Margin = new Thickness(12, 2, 8, 2);
            Put(grid, name, row, 0);

            Border? fill = null;
            if (s.SensorType is SensorType.Load or SensorType.Control or SensorType.Level)
            {
                fill = new Border
                {
                    CornerRadius = new CornerRadius(3),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = 0,
                    Background = Accent
                };
                var track = new Border
                {
                    Height = 6,
                    Width = 100,
                    CornerRadius = new CornerRadius(3),
                    Background = TrackBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 8, 0),
                    Child = fill
                };
                Put(grid, track, row, 1);
            }

            var val = Cell("—", right: true);
            val.FontWeight = FontWeights.SemiBold;
            var min = Cell("—", muted: true, right: true);
            var max = Cell("—", muted: true, right: true);
            Put(grid, val, row, 2);
            Put(grid, min, row, 3);
            Put(grid, max, row, 4);
            _rows.Add(new SensorRow(s, val, min, max, fill));
            row++;
        }

        panel.Children.Add(grid);
    }

    private TextBlock Cell(string text, bool muted = false, bool right = false) => new()
    {
        Text = text,
        Style = RowStyle,
        Foreground = muted ? Muted : TextBrush,
        HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        Margin = new Thickness(8, 2, 0, 2)
    };

    private static void Put(Grid g, UIElement el, int row, int col)
    {
        Grid.SetRow(el, row);
        Grid.SetColumn(el, col);
        g.Children.Add(el);
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            await Task.Run(() => _computer.Accept(_visitor));
            UpdateLive();
        }
        catch { }
        finally { _busy = false; }
    }

    private void UpdateLive()
    {
        foreach (var r in _rows)
        {
            var t = r.Sensor.SensorType;
            var v = r.Sensor.Value;
            r.Value.Text = Fmt(t, v);
            r.Min.Text = Fmt(t, r.Sensor.Min);
            r.Max.Text = Fmt(t, r.Sensor.Max);

            var brush = StateBrush(t, v);
            r.Value.Foreground = brush;
            if (r.Fill != null)
            {
                r.Fill.Width = v is null ? 0 : Math.Clamp(v.Value, 0f, 100f);
                r.Fill.Background = ReferenceEquals(brush, TextBrush) ? Accent : brush;
            }
        }

        if (_uptimeRun != null)
        {
            var ts = TimeSpan.FromMilliseconds(Environment.TickCount64);
            _uptimeRun.Text = $"{(int)ts.TotalDays} d {ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        }

        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref m))
        {
            if (_ramTotalRun != null) _ramTotalRun.Text = FmtBytes((long)m.ullTotalPhys);
            if (_ramFreeRun != null) _ramFreeRun.Text = FmtBytes((long)m.ullAvailPhys);
            if (_ramUsedRun != null)
                _ramUsedRun.Text = $"{FmtBytes((long)(m.ullTotalPhys - m.ullAvailPhys))} ({m.dwMemoryLoad}%)";
            if (_pageRun != null)
                _pageRun.Text = $"{FmtBytes((long)m.ullAvailPageFile)} / {FmtBytes((long)m.ullTotalPageFile)}";
        }
    }

    private Brush StateBrush(SensorType t, float? v)
    {
        if (v is null) return Muted;
        float x = v.Value;
        if (t == SensorType.Temperature) return x < 60 ? OkBrush : x < 80 ? WarnBrush : BadBrush;
        if (t == SensorType.Load) return x < 70 ? TextBrush : x < 90 ? WarnBrush : BadBrush;
        return TextBrush;
    }

    private StackPanel GetSection(string key, bool expanded = false)
    {
        if (_sections.TryGetValue(key, out var existing)) return existing;

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = Accent,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(new TextBlock
        {
            Text = SectionTitles[key],
            FontSize = 15,
            Foreground = TextBrush,
            VerticalAlignment = VerticalAlignment.Center
        });

        var panel = new StackPanel();
        SectionsPanel.Children.Add(new Expander
        {
            Header = header,
            Content = panel,
            IsExpanded = expanded
        });
        _sections[key] = panel;
        return panel;
    }

    private Run AddRow(Panel p, string label, string value)
    {
        p.Children.Add(p.Children.Count == 0
            ? new Separator { Margin = new Thickness(0, 0, 0, 10) }
            : new Separator());

        var run = new Run(value);
        var tb = new TextBlock { Style = RowStyle, TextWrapping = TextWrapping.Wrap };
        tb.Inlines.Add(new Run(label + ":  ") { Foreground = Muted });
        tb.Inlines.Add(run);
        p.Children.Add(tb);
        return run;
    }

    private static Brush MakeBrush(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    private static IEnumerable<IHardware> Flatten(IEnumerable<IHardware> list)
    {
        foreach (var h in list)
        {
            yield return h;
            foreach (var sub in Flatten(h.SubHardware)) yield return sub;
        }
    }

    private static List<Dictionary<string, object?>> Wmi(string query)
    {
        var result = new List<Dictionary<string, object?>>();
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            foreach (ManagementBaseObject obj in searcher.Get())
            {
                var d = new Dictionary<string, object?>();
                foreach (var prop in obj.Properties) d[prop.Name] = prop.Value;
                result.Add(d);
                obj.Dispose();
            }
        }
        catch { }
        return result;
    }

    private static string S(Dictionary<string, object?>? d, string key) =>
        d != null && d.TryGetValue(key, out var v) && v != null && v.ToString()!.Trim().Length > 0
            ? v.ToString()!.Trim()
            : "—";

    private static string Kb(Dictionary<string, object?> d, string key) =>
        long.TryParse(S(d, key), out long kb) && kb > 0 ? FmtBytes(kb * 1024) : "—";

    private static string ToDate(string wmiDate)
    {
        try { return ManagementDateTimeConverter.ToDateTime(wmiDate).ToString("yyyy-MM-dd"); }
        catch { return wmiDate; }
    }

    private static string FmtBytes(long b)
    {
        double v = b;
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:F1} {u[i]}";
    }

    private static string TypeTitle(SensorType t) => t switch
    {
        SensorType.Temperature => "Temperatures",
        SensorType.Load => "Load",
        SensorType.Clock => "Clocks",
        SensorType.Power => "Power",
        SensorType.Voltage => "Voltages",
        SensorType.Fan => "Fans",
        SensorType.Control => "Fan control",
        SensorType.Level => "Level",
        SensorType.Factor => "Factors",
        SensorType.Data => "Data",
        SensorType.SmallData => "Data (small)",
        SensorType.Throughput => "Throughput",
        _ => t.ToString()
    };

    private static string Fmt(SensorType t, float? v)
    {
        if (v is null) return "—";
        float x = v.Value;
        return t switch
        {
            SensorType.Temperature => $"{x:F1} °C",
            SensorType.Load or SensorType.Control or SensorType.Level => $"{x:F1} %",
            SensorType.Clock => $"{x:F0} MHz",
            SensorType.Power => $"{x:F1} W",
            SensorType.Voltage => $"{x:F3} V",
            SensorType.Fan => $"{x:F0} RPM",
            SensorType.Data => $"{x:F1} GB",
            SensorType.SmallData => $"{x:F0} MB",
            SensorType.Throughput => FmtBytes((long)x) + "/s",
            _ => $"{x:F2}"
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware) sub.Accept(this);
        }

        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}