using System.Security.Cryptography;
using HomeControl.Core.Security;

namespace HomeControl.Services;

/// <summary>Encrypts secrets with DPAPI so only the current Windows user can read them.</summary>
internal sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "HomeControl.Secrets.v1"u8.ToArray();

    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}
