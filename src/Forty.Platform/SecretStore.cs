using System.Runtime.InteropServices;
using System.Text.Json;

namespace Forty.Platform;

/// <summary>
/// Service keys and sign-in tokens, encrypted by Windows for your account (F121): the same protection
/// browsers use for saved passwords, so only your Windows account on this PC can read them. One small
/// file, %LOCALAPPDATA%\Forty\keys.bin, shared with the speech process (the format the speech spike's
/// setup-keys.cmd writes). A value is never printed, logged, put in a crash report or in the project.
/// When Forty is packaged, this moves to Windows' credential locker; the interface stays.
/// </summary>
public sealed class SecretStore
{
    readonly string _path;
    readonly object _gate = new();
    Dictionary<string, string>? _cache;

    public SecretStore(string path) => _path = path;

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Forty", "keys.bin");

    static bool Real(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim() != "your-key-here";

    /// <summary>The stored value, or null. Placeholder text counts as nothing.</summary>
    public string? Get(string name) => Load().TryGetValue(name, out var value) && Real(value) ? value : null;

    public bool Has(string name) => Get(name) != null;

    /// <summary>Stores a value, or removes it when <paramref name="value"/> is null. Removing the last one removes the file.</summary>
    public void Set(string name, string? value)
    {
        lock (_gate)
        {
            var all = new Dictionary<string, string>(Load());
            if (value == null) all.Remove(name); else all[name] = value.Trim();
            if (all.Count == 0) { if (File.Exists(_path)) File.Delete(_path); }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllBytes(_path, Transform(JsonSerializer.SerializeToUtf8Bytes(all), protect: true));
            }
            _cache = all;
        }
    }

    /// <summary>The names stored (never the values), for a settings page.</summary>
    public IReadOnlyList<string> Names => Load().Where(p => Real(p.Value)).Select(p => p.Key).ToList();

    public void Forget() { lock (_gate) _cache = null; }

    Dictionary<string, string> Load()
    {
        lock (_gate)
        {
            if (_cache != null) return _cache;
            _cache = new();
            try
            {
                if (File.Exists(_path))
                    _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(Transform(File.ReadAllBytes(_path), protect: false)) ?? new();
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
            {
                // From another account or PC, or damaged: it cannot be read, so it is treated as empty.
            }
            return _cache;
        }
    }

    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);

    static byte[] Transform(byte[] data, bool protect)
    {
        var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new Blob { Size = data.Length, Data = pinned.AddrOfPinnedObject() };
            bool ok = protect ? CryptProtectData(ref input, "Forty service keys", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out var output)
                              : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new InvalidOperationException("Windows could not " + (protect ? "encrypt" : "decrypt") + " the keys file.");
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { pinned.Free(); }
    }
}
