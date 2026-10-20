using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Lemon.Services;

/// <summary>
/// Mantiene visible la miniatura del archivo arrastrado (la dibuja el shell de Windows)
/// mientras el cursor está sobre nuestra ventana. Sin esto, la imagen "desaparece" al entrar.
/// </summary>
public static class DragImage
{
    [ComImport, Guid("4657278B-411B-11D2-839A-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropTargetHelper
    {
        void DragEnter(IntPtr hwndTarget, ComIDataObject dataObject, ref Native.POINT pt, int effect);
        void DragLeave();
        void DragOver(ref Native.POINT pt, int effect);
        void Drop(ComIDataObject dataObject, ref Native.POINT pt, int effect);
        void Show([MarshalAs(UnmanagedType.Bool)] bool show);
    }

    [ComImport, Guid("4657278A-411B-11D2-839A-00C04FD918D0")]
    private class DragDropHelper { }

    private static IDropTargetHelper? _helper;
    private static bool _failed;

    private static IDropTargetHelper? Helper
    {
        get
        {
            if (_helper is not null || _failed) return _helper;
            try { _helper = (IDropTargetHelper)new DragDropHelper(); }
            catch (Exception ex) { _failed = true; Log.Write("DragImage no disponible: " + ex.Message); }
            return _helper;
        }
    }

    public static void Enter(Window window, IDataObject data, DragDropEffects effect)
    {
        if (data is not ComIDataObject com) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        var pt = Native.CursorPosition();
        Safe(() => Helper?.DragEnter(hwnd, com, ref pt, (int)effect));
    }

    public static void Over(DragDropEffects effect)
    {
        var pt = Native.CursorPosition();
        Safe(() => Helper?.DragOver(ref pt, (int)effect));
    }

    public static void Leave() => Safe(() => Helper?.DragLeave());

    public static void Drop(IDataObject data, DragDropEffects effect)
    {
        if (data is not ComIDataObject com) { Leave(); return; }
        var pt = Native.CursorPosition();
        Safe(() => Helper?.Drop(com, ref pt, (int)effect));
    }

    private static void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Write("DragImage error: " + ex.Message); }
    }
}
