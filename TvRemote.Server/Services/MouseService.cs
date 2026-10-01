using TvRemote.Configuration;

namespace TvRemote.Services;
public interface IMouseService { void Move(int dx, int dy); void Click(string button); void Button(string button, bool down); void Scroll(int vertical, int horizontal); }
public sealed class MouseService(IInputService input, IConfigStore store) : IMouseService
{
    public void Move(int dx, int dy) => input.Mouse((int)Math.Round(dx * store.Current.MouseSensitivity), (int)Math.Round(dy * store.Current.MouseSensitivity), 1);
    public void Click(string button) { Button(button, true); Button(button, false); }
    public void Button(string button, bool down) => input.Mouse(0, 0, button == "left" ? (down ? 2u : 4u) : (down ? 8u : 16u));
    public void Scroll(int vertical, int horizontal)
    {
        if (vertical != 0) input.Mouse(0, 0, 0x800, vertical);
        if (horizontal != 0) input.Mouse(0, 0, 0x1000, horizontal);
    }
}
