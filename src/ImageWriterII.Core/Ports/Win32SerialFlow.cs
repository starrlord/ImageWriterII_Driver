using System.IO.Ports;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ImageWriterII.Core.Ports;

/// <summary>
/// Enables DSR-based output flow control (DCB.fOutxDsrFlow) on an open <see cref="SerialPort"/>.
/// System.IO.Ports only exposes CTS and XON/XOFF handshaking, but Windows and the FTDI/Prolific drivers
/// support DTR/DSR handshaking natively, and many Mac-style cables deliver the printer's DTR on the PC's DSR.
/// </summary>
internal static class Win32SerialFlow
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DCB
    {
        public uint DCBlength;
        public uint BaudRate;
        public uint Flags;
        public ushort wReserved;
        public ushort XonLim;
        public ushort XoffLim;
        public byte ByteSize;
        public byte Parity;
        public byte StopBits;
        public byte XonChar;
        public byte XoffChar;
        public byte ErrorChar;
        public byte EofChar;
        public byte EvtChar;
        public ushort wReserved1;
    }

    private const uint FOutxCtsFlow = 1u << 2;
    private const uint FOutxDsrFlow = 1u << 3;
    private const uint FDtrControlMask = 3u << 4;
    private const uint FDtrControlEnable = 1u << 4;
    private const uint FDsrSensitivity = 1u << 6;
    private const uint FRtsControlMask = 3u << 12;
    private const uint FRtsControlEnable = 1u << 12;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetCommState(SafeFileHandle hFile, ref DCB lpDCB);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetCommState(SafeFileHandle hFile, ref DCB lpDCB);

    /// <summary>Returns true when DSR flow control was switched on; false (with a reason) when it could not be.</summary>
    public static bool TryEnableDsrFlowControl(SerialPort port, out string reason)
    {
        reason = "";
        if (!OperatingSystem.IsWindows()) { reason = "not Windows"; return false; }
        try
        {
            var stream = port.BaseStream;
            var field = stream.GetType().GetField("_handle", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? stream.GetType().GetField("handle", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(stream) is not SafeFileHandle handle || handle.IsInvalid)
            {
                reason = "serial port handle not accessible in this runtime";
                return false;
            }
            var dcb = new DCB { DCBlength = (uint)Marshal.SizeOf<DCB>() };
            if (!GetCommState(handle, ref dcb))
            {
                reason = $"GetCommState failed ({Marshal.GetLastWin32Error()})";
                return false;
            }
            dcb.Flags &= ~(FOutxCtsFlow | FDsrSensitivity | FDtrControlMask | FRtsControlMask);
            dcb.Flags |= FOutxDsrFlow | FDtrControlEnable | FRtsControlEnable;
            if (!SetCommState(handle, ref dcb))
            {
                reason = $"SetCommState failed ({Marshal.GetLastWin32Error()}); the adapter driver may not support DSR handshaking";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }
}
