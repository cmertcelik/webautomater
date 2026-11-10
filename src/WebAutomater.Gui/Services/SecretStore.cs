using System.Security.Cryptography;
using System.Text;
using WebAutomater.Core.Diagnostics;

namespace WebAutomater.Gui;

/// <summary>
/// Ayar dosyasındaki gizli metinler (token, çerez, giriş bilgisi içeren istekler) için.
/// Windows'ta kullanıcıya özel şifrelenir (DPAPI): dosya başka bir kullanıcıya ya da bilgisayara kopyalansa çözülemez.
/// Diğer sistemlerde düz metin kalır. Eski (şifresiz) ayarlar olduğu gibi okunur, ilk kayıtta şifrelenir.
/// </summary>
public static class SecretStore
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = "WebAutomater.Ayarlar"u8.ToArray();

    public static string? Protect(string? text)
    {
        if (string.IsNullOrEmpty(text) || !OperatingSystem.IsWindows())
            return text;
        try
        {
            return Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(text), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException ex)
        {
            AppLog.Warning("Ayarlar", "Gizli bilgi şifrelenemedi; şifresiz kaydediliyor.", ex);
            return text;
        }
    }

    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal))
            return stored;
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Başka kullanıcı/bilgisayarda şifrelenmiş: kullanıcı isteği yeniden yapıştırmalı.
            AppLog.Warning("Ayarlar", "Kayıtlı gizli bilgi çözülemedi (başka kullanıcıda şifrelenmiş olabilir); yeniden girilmeli.", ex);
            return null;
        }
    }
}
