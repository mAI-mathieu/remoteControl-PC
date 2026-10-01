namespace TvRemote.Services;
public interface IMediaService { void Press(string action); }
public sealed class MediaService(IInputService input) : IMediaService
{
    public static readonly IReadOnlyDictionary<string, ushort> Keys = new Dictionary<string, ushort>
    { ["previous"] = 0xB1, ["play_pause"] = 0xB3, ["next"] = 0xB0, ["stop"] = 0xB2 };
    public void Press(string action) { input.Key(Keys[action]); input.Key(Keys[action], true); }
}
