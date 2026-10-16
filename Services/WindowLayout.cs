using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BeaconSetupManager.Services;

internal static class WindowLayout
{
    public static void FitToWorkArea(Window window, bool mainWindow = false)
    {
        void Fit()
        {
            Rect area = SystemParameters.WorkArea;
            if (PresentationSource.FromVisual(window) is HwndSource source && source.CompositionTarget != null)
            {
                var monitor = MonitorFromWindow(source.Handle, 2);
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref info))
                    area = new System.Windows.Media.MatrixTransform(source.CompositionTarget.TransformFromDevice).TransformBounds(new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top));
            }
            ApplyBounds(window, area, mainWindow);
        }
        window.SourceInitialized += (_, _) => Fit();
        window.Loaded += (_, _) => Fit();
        window.DpiChanged += (_, _) => Fit();
    }

    internal static void ApplyBounds(Window window, Rect area, bool mainWindow = false)
    {
        double width = Math.Max(1, area.Width - 24), height = Math.Max(1, area.Height - 24);
        window.MinWidth = Math.Min(window.MinWidth, width); window.MinHeight = Math.Min(window.MinHeight, height);
        window.MaxWidth = mainWindow ? double.PositiveInfinity : width;
        window.MaxHeight = mainWindow ? double.PositiveInfinity : height;
        if (!double.IsNaN(window.Width)) window.Width = Math.Min(window.Width, width);
        if (!double.IsNaN(window.Height)) window.Height = Math.Min(window.Height, height);
        if (window.IsLoaded && window.WindowState == WindowState.Normal)
        {
            window.Left = Math.Clamp(window.Left, area.Left + 12, Math.Max(area.Left + 12, area.Right - window.ActualWidth - 12));
            window.Top = Math.Clamp(window.Top, area.Top + 12, Math.Max(area.Top + 12, area.Bottom - window.ActualHeight - 12));
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
