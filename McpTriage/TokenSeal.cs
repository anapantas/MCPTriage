using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace McpTriage;

// Encrypts the tokens the library hands back, so the McpCredential row, or a token that reaches a browser or a log,
// is useless without the app's TokenKey. AES-256-GCM, with the key derived from TokenKey; plain tokens exist only inside a call.
internal static class TokenSeal
{
    private const string Prefix = "mcpt1.";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    public const int MinKeyLength = 32;

    public static byte[] Key(string tokenKey)
    {
        tokenKey = tokenKey?.Trim();
        if (string.IsNullOrEmpty(tokenKey) || tokenKey.Length < MinKeyLength)
            throw new McpTriageException("invalid_connection", $"TokenKey must be a secret of at least {MinKeyLength} characters, kept in a secret Setting; npm run tokens suggests one");
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(tokenKey), 32, Encoding.UTF8.GetBytes("McpTriage"), Encoding.UTF8.GetBytes("token seal v1"));
    }

    // The field name is bound to the ciphertext, so an encrypted access token can't be passed as the refresh token
    public static string Seal(byte[] key, string field, string token)
    {
        if (string.IsNullOrEmpty(token)) return token;
        var plain = Encoding.UTF8.GetBytes(token);
        var box = new byte[NonceSize + plain.Length + TagSize];
        RandomNumberGenerator.Fill(box.AsSpan(0, NonceSize));
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(box.AsSpan(0, NonceSize), plain, box.AsSpan(NonceSize, plain.Length), box.AsSpan(NonceSize + plain.Length), Encoding.UTF8.GetBytes(field));
        return Prefix + Base64Url.EncodeToString(box);
    }

    // An encrypted token is decrypted; a plain one (the bootstrap token from npm run tokens) is taken as is
    public static string Open(byte[] key, string field, string value)
    {
        if (value == null || !value.StartsWith(Prefix, StringComparison.Ordinal)) return value;
        try
        {
            var box = Base64Url.DecodeFromChars(value.AsSpan(Prefix.Length));
            var plain = new byte[box.Length - NonceSize - TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(box.AsSpan(0, NonceSize), box.AsSpan(NonceSize, plain.Length), box.AsSpan(NonceSize + plain.Length), plain, Encoding.UTF8.GetBytes(field));
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception err) when (err is CryptographicException or FormatException or OverflowException or ArgumentException)
        {
            throw new McpTriageException("token_key_mismatch", $"{field} can't be decrypted with this TokenKey: the key changed or the value was altered. "
                + "Delete the stored tokens so the bootstrap token is used again.");
        }
    }
}
