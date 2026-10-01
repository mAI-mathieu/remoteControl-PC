namespace TvRemote.Host;

public static class BrandIcon
{
    public static Icon Load()
    {
        using var stream = typeof(BrandIcon).Assembly.GetManifestResourceStream("TvRemote.app.ico")
            ?? throw new InvalidOperationException("App icon resource is missing.");
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }
}
