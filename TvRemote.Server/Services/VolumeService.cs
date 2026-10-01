namespace TvRemote.Services;
public interface IVolumeService { void Press(string action); }
public sealed class VolumeService(IInputService input) : IVolumeService
{
    public static readonly IReadOnlyDictionary<string, ushort> Keys = new Dictionary<string, ushort>
    { ["down"] = 0xAE, ["mute"] = 0xAD, ["up"] = 0xAF };
    public void Press(string action) { input.Key(Keys[action]); input.Key(Keys[action], true); }
}
