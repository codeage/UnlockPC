using System.Globalization;
using System.Reflection;
using System.Xml.Linq;

namespace WinUIKit;

/// <summary>
/// The library's own built-in text (e.g. accessible names of the caption buttons), in Chinese and English
/// (<c>Strings\zh-CN\Ui.resw</c>, <c>Strings\en-US\Ui.resw</c>, compiled into the library as embedded resources).
/// Host apps have their own localization systems; the library doesn't hook into them and just picks Chinese or English
/// by <see cref="UiHost.Language"/>. {app} in the text is replaced with <see cref="UiHost.AppName"/>.
/// Keys are grouped by prefix (e.g. Common_*, Caption_*).
/// </summary>
internal static partial class UiText
{
    private static Dictionary<string, string>? s_zh, s_en;

    private static Dictionary<string, string> Load(string lang)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        Assembly asm = typeof(UiText).Assembly;
        string name = $"WinUIKit.{lang}.Ui.resw";   // name is fixed in the project file (LogicalName)
        using Stream? stream = asm.GetManifestResourceStream(name);
        if (stream is null)
        {
            UiHost.WriteLog($"[WinUIKit] Missing text resource {lang}");
            return map;
        }
        foreach (XElement data in XDocument.Load(stream).Root!.Elements("data"))
        {
            if (data.Attribute("name")?.Value is { } key)
                map[key] = data.Element("value")?.Value ?? "";
        }
        return map;
    }

    /// <summary>Gets a string: falls back to English if the current language lacks it, then to the key itself (so a missing entry is obvious).</summary>
    internal static string Get(string key)
    {
        Dictionary<string, string> primary = UiHost.IsChinese ? (s_zh ??= Load("zh-CN")) : (s_en ??= Load("en-US"));
        if (!primary.TryGetValue(key, out string? text) && !(s_en ??= Load("en-US")).TryGetValue(key, out text))
            text = key;
        return text.Replace("{app}", UiHost.AppName, StringComparison.Ordinal);
    }

    /// <summary>A string with placeholders.</summary>
    internal static string Format(string key, params object?[] args) =>
        string.Format(Culture, Get(key), args);

    internal static CultureInfo Culture => CultureInfo.GetCultureInfo(UiHost.IsChinese ? "zh-CN" : "en-US");
}
