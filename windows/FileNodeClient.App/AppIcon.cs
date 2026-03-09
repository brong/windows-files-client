using System.Drawing;

namespace FileNodeClient.App;

/// <summary>
/// Loads the cached Fastmail icon for use as form/window icons.
/// </summary>
static class AppIcon
{
    private static Icon? _cached;
    private static bool _attempted;

    public static Icon? Get()
    {
        if (_attempted) return _cached;
        _attempted = true;

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FileNodeClient", "icon.ico");
            if (File.Exists(path))
                _cached = new Icon(path);
        }
        catch { /* fall back to default */ }

        return _cached;
    }

    /// <summary>
    /// Set the form's icon to the Fastmail icon if available.
    /// </summary>
    public static void Apply(Form form)
    {
        var icon = Get();
        if (icon != null)
            form.Icon = icon;
    }
}
