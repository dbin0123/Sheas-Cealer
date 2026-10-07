using Avalonia.Controls;
using Avalonia.Platform;
using Sheas_Cealer_Nix.Consts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Sheas_Cealer_Nix.Utils;

// 点 X 在 Linux 上必须是「隐藏到托盘」，和 win/mac 一致。但 Cinnamon/muffin 对声明了
// _NET_WM_SYNC_REQUEST 的窗口不发送 WM_DELETE_WINDOW，而是直接 XKillClient：实测进程 0.05 秒内消失，
// Window.Closing / Closed / lifetime.ShutdownRequested 一个都没触发，托盘和 QuitAsync 漏斗全被绕过。
// 帧同步只在拖拽缩放时有意义（防内容跟不上窗口框），而主窗口 min==max 根本不可缩放，
// 所以把这个 atom 摘掉没有任何代价，只换来「关窗口」这件事能被我们接住。
// Avalonia 11.2.8 的 X11PlatformOptions 里没有关掉帧同步的开关，只能在窗口建好后自己改属性。
//
// 注意别用 XGetWMProtocols/XSetWMProtocols：它们走 libX11 硬编码的 WM_PROTOCOLS 常量（0x27），
// 而 X server 只预建 1..28 号 atom，WM_PROTOCOLS 实际是谁先 intern 谁拿号（本机实测 0x165），
// 0x27 在那儿是 WM_NAME。只能按名字 intern 之后用 XGetWindowProperty/XChangeProperty。
internal static class X11CloseProtocol
{
    private const int PropModeReplace = 0;

    internal static void StripFrameSync(Window window)
    {
        if (!OperatingSystem.IsLinux())
            return;

        try
        {
            StripOnX11Display(window);
        }
        catch (Exception ex)
        {
            // 纯 X11 互操作，失败只意味着「维持原样」，不能把启动流程带崩；但必须落盘，
            // 否则 Wayland-only 或者 libX11 缺失的环境里又变成一个只能靠猜的静默降级。
            try
            {
                Directory.CreateDirectory(MainConst.DataDir);
                File.AppendAllText(MainConst.GuiErrorLogPath, $"{DateTime.Now:O} x11 frame-sync strip failed, close may be killed by the WM: {ex.Message}{Environment.NewLine}");
            }
            catch
            {
            }
        }
    }

    private static void StripOnX11Display(Window window)
    {
        // Wayland 会话下 XOpenDisplay 返回 null；非 X11 后端也拿不到 x11 句柄。
        IntPtr display = XOpenDisplay(IntPtr.Zero);

        if (display == IntPtr.Zero)
            return;

        try
        {
            IPlatformHandle? handle = window.TryGetPlatformHandle();

            if (handle is null || handle.HandleDescriptor != "XID" || handle.Handle == IntPtr.Zero)
                return;

            nint xid = handle.Handle;
            nint protocols = XInternAtom(display, "WM_PROTOCOLS", false);
            nint syncAtom = XInternAtom(display, "_NET_WM_SYNC_REQUEST", true);
            nint atomType = XInternAtom(display, "ATOM", false);

            if (syncAtom == 0)
                return;

            // 整段按名字取 atom，重写时把类型统一成 ICCCM 要求的 ATOM。
            if (XGetWindowProperty(display, xid, protocols, 0L, 64L, false, 0,
                    out _, out int format, out nint nitems, out _, out IntPtr value) != 0)
                return;

            List<nint> kept = [];

            try
            {
                if (format != 32 || value == IntPtr.Zero)
                    return;

                // format 32 的数据在 Xlib 里按 long（8 字节）返回，不是 4 字节。
                for (int i = 0; i < (int)nitems; i++)
                {
                    nint atom = Marshal.ReadIntPtr(value, i * IntPtr.Size);

                    if (atom != syncAtom)
                        kept.Add(atom);
                }
            }
            finally
            {
                XFree(value);
            }

            if (kept.Count == (int)nitems)
                return;

            IntPtr buffer = Marshal.AllocHGlobal(Math.Max(kept.Count, 1) * IntPtr.Size);

            try
            {
                for (int i = 0; i < kept.Count; i++)
                    Marshal.WriteIntPtr(buffer, i * IntPtr.Size, kept[i]);

                XChangeProperty(display, xid, protocols, atomType, 32, PropModeReplace, buffer, kept.Count);
                XFlush(display);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    [DllImport("libX11.so.6", EntryPoint = "XOpenDisplay")]
    private static extern IntPtr XOpenDisplay(IntPtr displayName);

    [DllImport("libX11.so.6", EntryPoint = "XCloseDisplay")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6", EntryPoint = "XFlush")]
    private static extern int XFlush(IntPtr display);

    [DllImport("libX11.so.6", EntryPoint = "XInternAtom")]
    private static extern nint XInternAtom(IntPtr display, string atomName, [MarshalAs(UnmanagedType.I1)] bool onlyIfExists);

    [DllImport("libX11.so.6", EntryPoint = "XFree")]
    private static extern int XFree(IntPtr data);

    [DllImport("libX11.so.6", EntryPoint = "XGetWindowProperty")]
    private static extern int XGetWindowProperty(
        IntPtr display,
        nint window,
        nint property,
        long offset,
        long length,
        [MarshalAs(UnmanagedType.I1)] bool delete,
        nint requestType,
        out nint actualTypeReturn,
        out int actualFormatReturn,
        out nint nitemsReturn,
        out nint bytesAfterReturn,
        out IntPtr propReturn);

    [DllImport("libX11.so.6", EntryPoint = "XChangeProperty")]
    private static extern int XChangeProperty(
        IntPtr display,
        nint window,
        nint property,
        nint type,
        int format,
        int mode,
        IntPtr data,
        int nelements);
}
