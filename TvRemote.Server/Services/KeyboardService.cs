namespace TvRemote.Services;
public interface IKeyboardService { void Press(string key, IReadOnlyList<string> modifiers); void Text(string value); void Edit(int before, int remove, string value, int after); }
public sealed class KeyboardService(IInputService input) : IKeyboardService
{
    public static readonly IReadOnlyDictionary<string, ushort> Keys = new Dictionary<string, ushort>
    {
        ["ENTER"] = 0x0D, ["BACKSPACE"] = 8, ["DELETE"] = 0x2E, ["ESCAPE"] = 0x1B, ["TAB"] = 9,
        ["LEFT"] = 0x25, ["UP"] = 0x26, ["RIGHT"] = 0x27, ["DOWN"] = 0x28, ["HOME"] = 0x24,
        ["END"] = 0x23, ["PAGEUP"] = 0x21, ["PAGEDOWN"] = 0x22, ["SPACE"] = 0x20,
        ["CTRL"] = 0x11, ["ALT"] = 0x12, ["SHIFT"] = 0x10, ["WIN"] = 0x5B,
        ["F4"] = 0x73, ["F5"] = 0x74
    };
    public static bool ValidKey(string key) => Keys.ContainsKey(key) || key.Length == 1 && key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9';
    public void Press(string key, IReadOnlyList<string> modifiers)
    {
        var vk = Keys.TryGetValue(key, out var special) ? special : (ushort)key[0];
        var held = new List<ushort>();
        try
        {
            foreach (var modifier in modifiers) { input.Key(Keys[modifier]); held.Add(Keys[modifier]); }
            input.Key(vk); input.Key(vk, true);
        }
        finally { foreach (var modifier in held.AsEnumerable().Reverse()) input.Key(modifier, true); }
    }
    public void Text(string value)
    {
        // UTF-16 code units preserve accents and surrogate pairs (emoji support depends on the target app).
        foreach (var chunk in value.Chunk(128))
            input.BatchKeys(chunk.SelectMany(c => new[] { ((ushort)c, false, true), ((ushort)c, true, true) }));
    }
    public void Edit(int before, int remove, string value, int after)
    {
        void Repeat(ushort key, int count)
        {
            foreach (var chunk in Enumerable.Range(0, count).Chunk(128))
                input.BatchKeys(chunk.SelectMany(_ => new[] { (key, false, false), (key, true, false) }));
        }
        void Move(int delta) => Repeat(Keys[delta < 0 ? "LEFT" : "RIGHT"], Math.Abs(delta));
        Move(before); Repeat(Keys["BACKSPACE"], remove);
        var lines = value.Split('\n');
        for (var i = 0; i < lines.Length; i++) { if (i > 0) Press("ENTER", []); Text(lines[i]); }
        Move(after);
    }
}
