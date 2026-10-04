using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace Inpaint.App;

/// <summary>
/// macOS Dock 图标：`dotnet run` 裸进程没有 .app bundle，LaunchServices 读不到
/// Info.plist 的 CFBundleIconFile，Dock 只显示通用图标。开发期在启动完成后把内嵌
/// icns 设给 NSApplication 补上；正式包由 bundle 提供同一素材，此设置是覆盖而非幂等。
/// 注意运行时图标不走 macOS 26 给 bundle 图标自动加的圆角蒙版（Windows 的 .ico
/// 同样不加工），圆角必须烤在素材里，两侧显示才一致。
/// Avalonia 未公开该 API，经 libobjc 手发 ObjC 消息实现；Dock 图标纯外观，失败静默不阻断启动。
/// </summary>
internal static class MacDockIcon
{
    private const string LibObjC = "/usr/lib/libobjc.dylib";

    public static void TrySetFromEmbeddedIcon()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Inpaint.App/Assets/app-icon.icns"));
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            SetApplicationIcon(memory.ToArray());
        }
        catch
        {
            // 图标缺失/无窗口服务端等任何失败都不影响功能
        }
    }

    private static unsafe void SetApplicationIcon(byte[] icns)
    {
        fixed (byte* bytes = icns)
        {
            var data = MsgSendBytes(ObjCGetClass("NSData"), Selector("dataWithBytes:length:"),
                (IntPtr)bytes, (UIntPtr)icns.LongLength);
            if (data == IntPtr.Zero) return;

            var image = MsgSendObject(MsgSend(ObjCGetClass("NSImage"), Selector("alloc")),
                Selector("initWithData:"), data);
            if (image == IntPtr.Zero) return;

            var app = MsgSend(ObjCGetClass("NSApplication"), Selector("sharedApplication"));
            if (app != IntPtr.Zero)
                MsgSendObject(app, Selector("setApplicationIconImage:"), image);
        }
    }

    private static IntPtr ObjCGetClass(string name) => objc_getClass(name);

    private static IntPtr Selector(string name) => sel_registerName(name);

    [DllImport(LibObjC)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(LibObjC)]
    private static extern IntPtr sel_registerName(string name);

    // objc_msgSend 按被调方法签名收参，须按参数个数/类型分别声明（EntryPoint 同名不同托管签名）

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSendObject(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSendBytes(IntPtr receiver, IntPtr selector, IntPtr bytes, UIntPtr length);
}
