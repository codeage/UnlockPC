using System.Text.Encodings.Web;
using System.Text.Json;

namespace WinUIKit;

/// <summary>
/// Where the library keeps the window state it remembers (size of resizable <see cref="ModalNavWindow"/>s, the page
/// that was open last, which expanders were open). The application decides where that lives: hand the library an
/// implementation through <see cref="UiHost.Settings"/>.
/// <para>
/// Use one of the ready-made stores in <see cref="UiSettingsStores"/>, or implement this interface to keep the state
/// in the application's own settings file. Keys look like <c>Prefix.WidthDip</c>, <c>Prefix.HeightDip</c>,
/// <c>Prefix.LastPage</c> and <c>Prefix.Expander.Name</c>, where the prefix is the one the application passes to the
/// window; see the README for the full list and the value formats.
/// </para>
/// <para>
/// Contract: all members are called on the UI thread. Implementations may throw; the library logs the error through
/// <see cref="UiHost.WriteLog"/> and carries on (reads fall back to the default value, writes are dropped), so a
/// broken settings file never stops a window from opening or closing.
/// </para>
/// </summary>
public interface IUiSettingsStore
{
    /// <summary>Returns the stored value, or <c>null</c> when the key has never been written.</summary>
    string? Read(string key);

    /// <summary>
    /// Records a value. It does not need to reach the disk yet: the library calls <see cref="Save"/> after each
    /// batch of writes.
    /// </summary>
    void Write(string key, string value);

    /// <summary>Persists the values written so far. Stores that persist on every write can leave this empty.</summary>
    void Save();
}

/// <summary>Ready-made <see cref="IUiSettingsStore"/> implementations.</summary>
public static class UiSettingsStores
{
    /// <summary>
    /// Adapts three functions of the application's own settings system. <paramref name="save"/> may be <c>null</c>
    /// when <paramref name="write"/> already persists.
    /// </summary>
    public static IUiSettingsStore FromDelegates(Func<string, string?> read, Action<string, string> write, Action? save = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);
        return new DelegateStore(read, write, save);
    }

    /// <summary>
    /// Keeps the state in a JSON file of its own (one object of string values). The application chooses the path,
    /// typically under <c>%LocalAppData%\&lt;app&gt;\</c>. The file is read once, on first use; saving replaces it
    /// atomically and keeps the previous version next to it as <c>.bak</c>. When the file cannot be read, the backup
    /// is used; when neither can be read, the store starts empty.
    /// </summary>
    public static IUiSettingsStore JsonFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new JsonFileStore(Path.GetFullPath(path));
    }

    /// <summary>Keeps the state in memory only (lost when the process exits). Useful for tests.</summary>
    public static IUiSettingsStore InMemory() => new MemoryStore();

    private sealed class DelegateStore(Func<string, string?> read, Action<string, string> write, Action? save) : IUiSettingsStore
    {
        public string? Read(string key) => read(key);
        public void Write(string key, string value) => write(key, value);
        public void Save() => save?.Invoke();
    }

    private sealed class MemoryStore : IUiSettingsStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public string? Read(string key) => _values.TryGetValue(key, out string? value) ? value : null;
        public void Write(string key, string value) => _values[key] = value;
        public void Save() { }
    }

    internal sealed class JsonFileStore(string path) : IUiSettingsStore
    {
        private static readonly JsonWriterOptions WriterOptions = new()
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // keep non-ASCII text readable in the file
        };

        private Dictionary<string, string>? _values;
        private bool _dirty;

        internal string FilePath => path;
        internal string BackupPath => path + ".bak";

        public string? Read(string key) => Values.TryGetValue(key, out string? value) ? value : null;

        public void Write(string key, string value)
        {
            Dictionary<string, string> values = Values;
            if (values.TryGetValue(key, out string? old) && old == value)
                return;
            values[key] = value;
            _dirty = true;
        }

        public void Save()
        {
            if (!_dirty || _values is null)
                return;
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Write a temporary file next to the target, then swap it in: a crash mid-write leaves only the
            // temporary file behind, never a half-written settings file.
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, Serialize(_values));
            if (File.Exists(path))
                File.Replace(temp, path, BackupPath, ignoreMetadataErrors: true);
            else
                File.Move(temp, path, overwrite: true);
            _dirty = false;
        }

        private Dictionary<string, string> Values => _values ??= Load();

        private Dictionary<string, string> Load()
        {
            bool mainExists = File.Exists(path);
            bool backupExists = File.Exists(BackupPath);
            if (!mainExists && !backupExists)
                return new Dictionary<string, string>(StringComparer.Ordinal);   // first run

            string? mainError = "missing";
            if (mainExists && TryParseFile(path, out Dictionary<string, string>? values, out mainError))
                return values;

            string? backupError = "missing";
            if (backupExists && TryParseFile(BackupPath, out Dictionary<string, string>? backup, out backupError))
            {
                UiHost.WriteLog($"[Settings] {path} could not be read ({mainError}); using the backup.");
                _dirty = true;   // the next save rewrites the main file from the backup's content
                return backup;
            }
            UiHost.WriteLog($"[Settings] {path} could not be read ({mainError}; backup: {backupError}); starting with defaults.");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        private static bool TryParseFile(string file, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Dictionary<string, string>? values, out string? error)
        {
            try
            {
                values = Parse(File.ReadAllBytes(file));
                error = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
            {
                values = null;
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        /// <summary>Parses one JSON object; non-string values are kept as their raw JSON text.</summary>
        internal static Dictionary<string, string> Parse(byte[] utf8)
        {
            ReadOnlyMemory<byte> json = utf8;
            if (json.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
                json = json[3..];   // a file saved by hand in an editor that adds a UTF-8 BOM
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new FormatException("The root of the settings file is not a JSON object.");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                values[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()!
                    : property.Value.GetRawText();
            }
            return values;
        }

        internal static byte[] Serialize(Dictionary<string, string> values)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, WriterOptions))
            {
                writer.WriteStartObject();
                foreach (KeyValuePair<string, string> pair in values.OrderBy(p => p.Key, StringComparer.Ordinal))
                    writer.WriteString(pair.Key, pair.Value);
                writer.WriteEndObject();
            }
            return stream.ToArray();
        }
    }
}
