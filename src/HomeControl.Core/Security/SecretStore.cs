using System.Text;
using System.Text.Json;

namespace HomeControl.Core.Security;

/// <summary>Encrypts data at rest. The Windows app uses DPAPI (current user scope).</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] data);

    byte[] Unprotect(byte[] data);
}

/// <summary>
/// Small key/value store for secrets (OAuth client secret, refresh token). The whole
/// dictionary is serialized to JSON and encrypted with an <see cref="ISecretProtector"/>.
/// </summary>
public sealed class SecretStore
{
    public const string ClientIdKey = "google.clientId";
    public const string ClientSecretKey = "google.clientSecret";
    public const string RefreshTokenKey = "google.refreshToken";

    private readonly string _filePath;
    private readonly ISecretProtector _protector;
    private readonly object _gate = new();
    private Dictionary<string, string>? _values;

    public SecretStore(string filePath, ISecretProtector protector)
    {
        _filePath = filePath;
        _protector = protector;
    }

    public string? Get(string key)
    {
        lock (_gate)
        {
            return Values.TryGetValue(key, out var value) ? value : null;
        }
    }

    public void Set(string key, string? value)
    {
        lock (_gate)
        {
            var changed = string.IsNullOrEmpty(value)
                ? Values.Remove(key)
                : !Values.TryGetValue(key, out var existing) || existing != value;

            if (!string.IsNullOrEmpty(value))
            {
                Values[key] = value;
            }

            if (changed)
            {
                Persist();
            }
        }
    }

    private Dictionary<string, string> Values => _values ??= LoadValues();

    private Dictionary<string, string> LoadValues()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            var plain = _protector.Unprotect(File.ReadAllBytes(_filePath));
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? [];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Unreadable (other user, other machine, or damaged): start over. The user will
            // simply be asked to sign in again.
            return [];
        }
    }

    private void Persist()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Values));
        var tempPath = _filePath + ".tmp";
        File.WriteAllBytes(tempPath, _protector.Protect(plain));
        File.Move(tempPath, _filePath, overwrite: true);
    }
}
