using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TvRemote.Services;

public interface IInputService { void Mouse(int dx, int dy, uint flags, int data = 0); void Key(ushort key, bool up = false, bool unicode = false); void BatchKeys(IEnumerable<(ushort Key, bool Up, bool Unicode)> keys); }
public sealed class InputService : IInputService
{
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Vk, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    private static void Send(Input[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows blocked input. Focus a normal, non-elevated application.");
    }
    public void Mouse(int dx, int dy, uint flags, int data = 0) => Send([new() { Data = new() { Mouse = new() { X = dx, Y = dy, Flags = flags, Data = unchecked((uint)data) } } }]);
    private static Input MakeKey(ushort key, bool up, bool unicode) => new()
    {
        Type = 1, Data = new() { Keyboard = new() { Vk = unicode ? (ushort)0 : key, Scan = unicode ? key : (ushort)0,
            Flags = (up ? 2u : 0) | (unicode ? 4u : key is >= 0x21 and <= 0x2E or 0x5B or 0x5C ? 1u : 0) } }
    };
    public void Key(ushort key, bool up = false, bool unicode = false) => Send([MakeKey(key, up, unicode)]);
    public void BatchKeys(IEnumerable<(ushort Key, bool Up, bool Unicode)> keys) => Send(keys.Select(k => MakeKey(k.Key, k.Up, k.Unicode)).ToArray());
}
