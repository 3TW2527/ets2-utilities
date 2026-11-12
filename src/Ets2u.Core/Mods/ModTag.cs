using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Ets2u.Core.Mods;

/// <summary>
/// A short suffix that makes this player's generated mod files differ from everyone else's
/// (e.g. ets2u_coach_3fa9c1.scs). Made once and kept in settings, so it doesn't change afterwards.
/// </summary>
public static class ModTag
{
    /// <summary>From the signed-in Steam account (hashed, so the account ID isn't published in file names), else random.</summary>
    public static string Create(IReadOnlyList<string> cloudProfileRoots) =>
        FromAccount(SteamAccount(cloudProfileRoots)) ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();

    public static string? FromAccount(string? accountId) =>
        string.IsNullOrEmpty(accountId)
            ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("ets2u:" + accountId)))[..6].ToLowerInvariant();

    /// <summary>The Steam account currently signed in, or the only one with ETS2 profiles on this PC.</summary>
    static string? SteamAccount(IReadOnlyList<string> cloudProfileRoots)
    {
        if (OperatingSystem.IsWindows()
            && Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", null) is int active
            && active != 0)
            return active.ToString();
        // Roots look like userdata/<account>/227300/remote/profiles.
        var accounts = cloudProfileRoots
            .Select(r => Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(r)))))
            .Distinct()
            .ToList();
        return accounts.Count == 1 ? accounts[0] : null;
    }
}
