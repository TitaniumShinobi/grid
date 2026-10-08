using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Grid.Auth.Features;

/// <summary>
/// Windows DPAPI token store (desktop production store).
///
/// The bag JSON is protected with CryptProtectData at CurrentUser scope and
/// written to %LOCALAPPDATA%\Grid\auth\{key}.json. No OAuth client secret is
/// ever stored — the bag holds access/refresh tokens of the public client.
///
/// This type is Windows-only: on any other OS it throws
/// PlatformNotSupportedException from the constructor, so the Grid app can
/// keep it behind a Windows platform guard with no cross-platform surprises.
/// </summary>
public sealed class DpapiTokenStore : ITokenStore
{
    public const string ScopeName = "Grid.Auth.TokenBag";
    private readonly string _directory;

    public DpapiTokenStore(string? baseDirectory = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI token store requires Windows.");
        }
        _directory = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Grid", "auth");
        Directory.CreateDirectory(_directory);
    }

    public Task<TokenBag?> LoadAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var file = PathFor(key);
        if (!File.Exists(file)) return Task.FromResult<TokenBag?>(null);
        byte[] plain;
        try
        {
            var protectedBlob = File.ReadAllBytes(file);
            plain = Unprotect(protectedBlob);
        }
        catch (CryptographicException)
        {
            return Task.FromResult<TokenBag?>(null);
        }
        var json = System.Text.Encoding.UTF8.GetString(plain);
        return Task.FromResult<TokenBag?>(TokenBagJson.Deserialize(json));
    }

    public Task SaveAsync(string key, TokenBag bag, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var json = TokenBagJson.Serialize(bag);
        var protectedBlob = Protect(System.Text.Encoding.UTF8.GetBytes(json));
        var file = PathFor(key);
        var temp = file + ".tmp";
        File.WriteAllBytes(temp, protectedBlob);
        File.Move(temp, file, overwrite: true);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var file = PathFor(key);
        if (File.Exists(file)) File.Delete(file);
        return Task.CompletedTask;
    }

    private string PathFor(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Token store key is required.", nameof(key));
        var safe = string.Concat(key.Select(c => (c is >= 'a' and <= 'z') || (c is >= 'A' and <= 'Z') || (c is >= '0' and <= '9') || c == '-' || c == '_' ? c : '_'));
        return Path.Combine(_directory, $"{safe}.json");
    }

    internal static byte[] Protect(byte[] plain)
    {
        var inBlob = Marshal.AllocHGlobal(plain.Length);
        try
        {
            Marshal.Copy(plain, 0, inBlob, plain.Length);
            var input = new DataBlob { cbData = plain.Length, pbData = inBlob };
            var output = default(DataBlob);
            if (!CryptProtectData(ref input, ScopeName, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output))
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(output.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob);
        }
    }

    internal static byte[] Unprotect(byte[] protectedData)
    {
        var inBlob = Marshal.AllocHGlobal(protectedData.Length);
        try
        {
            Marshal.Copy(protectedData, 0, inBlob, protectedData.Length);
            var input = new DataBlob { cbData = protectedData.Length, pbData = inBlob };
            var output = default(DataBlob);
            if (!CryptUnprotectData(ref input, out _, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output))
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(output.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        ref DataBlob pDataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        out string? ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        ref DataBlob pDataOut);
}

/// <summary>Internal JSON (de)serialization for the token bag. Kept separate so
/// the store has no dependency on the app serialization policy.</summary>
internal static class TokenBagJson
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web);

    public static string Serialize(TokenBag bag)
        => System.Text.Json.JsonSerializer.Serialize(bag, Options);

    public static TokenBag? Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var bag = System.Text.Json.JsonSerializer.Deserialize<TokenBag>(json, Options);
            if (bag is null || string.IsNullOrEmpty(bag.AccessToken)) return null;
            return bag;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}