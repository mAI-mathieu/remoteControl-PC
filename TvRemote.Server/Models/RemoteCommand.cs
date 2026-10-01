using System.Text.Json;
using TvRemote.Services;

namespace TvRemote.Models;
public sealed record RemoteCommand(string Type, int Dx = 0, int Dy = 0, string? Button = null, int Delta = 0, int Horizontal = 0,
    string? Key = null, string[]? Modifiers = null, string? Value = null, string? Action = null, string? Id = null, bool Confirm = false);

public static class CommandParser
{
    public const int MaxMessageBytes = 65_536;
    public static RemoteCommand Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > MaxMessageBytes) throw new FormatException("Message too large.");
        using var document = JsonDocument.Parse(utf8.ToArray(), new() { MaxDepth = 4 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Expected an object.");
        var properties = root.EnumerateObject().Select(p => p.Name).ToArray();
        if (properties.Distinct().Count() != properties.Length) throw new FormatException("Duplicate field.");
        string String(string name, int max = 60)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Length > max)
                throw new FormatException($"Invalid {name}.");
            return text;
        }
        int Number(string name, int limit, bool optional = false)
        {
            if (optional && !root.TryGetProperty(name, out _)) return 0;
            if (!root.TryGetProperty(name, out var value) || !value.TryGetInt32(out var number) || number < -limit || number > limit)
                throw new FormatException($"Invalid {name}.");
            return number;
        }
        var type = String("type");
        string[] allowed = type switch
        {
            "mouse_move" => ["type", "dx", "dy"],
            "mouse_click" or "mouse_down" or "mouse_up" => ["type", "button"],
            "scroll" => ["type", "delta", "horizontal"],
            "key" => ["type", "key", "modifiers"],
            "text" => ["type", "value"],
            "volume" => ["type", "action"],
            "app" => ["type", "id"],
            "playnite_close" or "ping" or "release" => ["type"],
            "power" => ["type", "action", "confirm"],
            _ => throw new FormatException("Unknown command.")
        };
        if (properties.Except(allowed).Any()) throw new FormatException("Unexpected field.");
        switch (type)
        {
            case "mouse_move": return new(type, Dx: Number("dx", 2000), Dy: Number("dy", 2000));
            case "mouse_click": case "mouse_down": case "mouse_up":
                var button = String("button");
                if (button is not ("left" or "right")) throw new FormatException("Invalid mouse button.");
                return new(type, Button: button);
            case "scroll": return new(type, Delta: Number("delta", 2400), Horizontal: Number("horizontal", 2400, true));
            case "key":
                var key = String("key");
                if (!KeyboardService.ValidKey(key) || key is "CTRL" or "ALT" or "SHIFT" or "WIN") throw new FormatException("Unsupported key.");
                string[] mods = [];
                if (root.TryGetProperty("modifiers", out var element))
                {
                    if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > 4) throw new FormatException("Invalid modifiers.");
                    mods = element.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new FormatException("Invalid modifier.")).ToArray();
                    if (mods.Distinct().Count() != mods.Length || mods.Any(m => m is not ("CTRL" or "ALT" or "SHIFT" or "WIN"))) throw new FormatException("Invalid modifier.");
                }
                if (key == "DELETE" && mods.Contains("CTRL") && mods.Contains("ALT")) throw new FormatException("Secure attention sequence is unsupported.");
                return new(type, Key: key, Modifiers: mods);
            case "text":
                var text = String("value", 10_000);
                if (text.Length == 0 || text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))) throw new FormatException("Invalid text.");
                for (var i = 0; i < text.Length; i++)
                {
                    if (char.IsHighSurrogate(text[i])) { if (++i >= text.Length || !char.IsLowSurrogate(text[i])) throw new FormatException("Invalid Unicode."); }
                    else if (char.IsLowSurrogate(text[i])) throw new FormatException("Invalid Unicode.");
                }
                return new(type, Value: text);
            case "app": return new(type, Id: String("id"));
            case "volume":
                var action = String("action");
                if (!VolumeService.Keys.ContainsKey(action)) throw new FormatException("Unsupported action.");
                return new(type, Action: action);
            case "power":
                var power = String("action");
                if (power is not ("sleep" or "lock" or "restart" or "shutdown")) throw new FormatException("Unsupported power action.");
                var confirmed = root.TryGetProperty("confirm", out var confirm) && confirm.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("confirm", out confirm) && confirm.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new FormatException("Invalid confirmation.");
                if (power is "restart" or "shutdown" && !confirmed) throw new FormatException("Power action requires confirmation.");
                return new(type, Action: power, Confirm: confirmed);
            default: return new(type);
        }
    }
}
