using System.Runtime.InteropServices;

namespace Lemon.Services;

/// <summary>Llamadas Win32 compartidas.</summary>
public static class Native
{
    public const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_SHIFT = 0x10, VK_CONTROL = 0x11;
    private const int SM_SWAPBUTTON = 23;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    public static bool IsDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    /// <summary>Botón principal FÍSICO (respeta zurdos con botones invertidos).</summary>
    public static bool IsPrimaryMouseDown() =>
        IsDown(GetSystemMetrics(SM_SWAPBUTTON) != 0 ? VK_RBUTTON : VK_LBUTTON);

    public static POINT CursorPosition()
    {
        GetCursorPos(out var p);
        return p;
    }

    /// <summary>
    /// Mueve el cursor 1 px y lo regresa. OLE solo vuelve a buscar la ventana destino cuando el mouse
    /// se mueve, así que esto hace que nuestra capa reciba DragEnter aunque tengas el mouse quieto.
    /// </summary>
    public static void NudgeCursor()
    {
        var p = CursorPosition();
        SetCursorPos(p.X + 1, p.Y);
        SetCursorPos(p.X, p.Y);
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
}
