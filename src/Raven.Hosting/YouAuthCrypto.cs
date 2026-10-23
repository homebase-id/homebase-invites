using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Raven.Hosting;

/// <summary>
/// The relying party's half of YouAuth's key exchange (odin-core docs/youauth-unified-authorization.md, steps
/// [010], [090] and [150]), with .NET's own crypto, matching odin-core byte for byte:
/// <list type="bullet">
/// <item>Public keys are P-384 JWKs, <c>{"kty":"EC","crv":"P-384","x":…,"y":…}</c> with base64url coordinates at
/// the field length, the JSON itself base64url-encoded (<c>EccPublicKeyData.PublicKeyJwkBase64Url</c>).</item>
/// <item>The exchange secret is HKDF-SHA256(ECDH X coordinate, zero-padded to 48 bytes; the identity's salt; no
/// info) to 16 bytes (<c>EccFullKeyData.GetEcdhSharedSecret</c>, <c>HashUtil.Hkdf</c>).</item>
/// <item>The token request's <c>secret_digest</c> is base64(SHA-256(exchange secret)).</item>
/// <item>The token fields are sealed with AES-128 under the exchange secret: GCM with the first 12 of the 16 IV
/// bytes as nonce and the 16-byte tag appended (<c>Odin.Core.Cryptography.Crypto.AesGcm</c>).</item>
/// </list>
/// </summary>
public static class YouAuthCrypto
{
    public const string CipherAesGcm = "aes-gcm";
    public const string CipherAesCbc = "aes-cbc";

    private const int GcmNonceSize = 12;
    private const int GcmTagSize = 16;
    private const int ExchangeSecretSize = 16;

    public static ECDiffieHellman NewKey() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);

    /// <summary>The key's public half as YouAuth sends it: a base64url-encoded P-384 JWK</summary>
    public static string PublicKeyJwkBase64Url(ECDiffieHellman key)
    {
        var q = key.ExportParameters(false).Q;
        // Member order and compact JSON as odin-core writes it; ExportParameters gives the coordinates at field length
        var jwk = JsonSerializer.Serialize(new
        {
            kty = "EC",
            crv = "P-384",
            x = Base64Url.EncodeToString(q.X),
            y = Base64Url.EncodeToString(q.Y),
        });
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(jwk));
    }

    /// <summary>A base64url-encoded P-384 JWK, the other party's public key; throws when it is not one</summary>
    public static ECDiffieHellmanPublicKey ReadPublicKeyJwkBase64Url(string jwkBase64Url)
    {
        using var json = JsonDocument.Parse(Base64Url.DecodeFromChars(jwkBase64Url));
        var root = json.RootElement;
        if (root.GetProperty("kty").GetString() != "EC" || root.GetProperty("crv").GetString() != "P-384")
        {
            throw new CryptographicException("Not a P-384 EC public key");
        }

        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP384,
            Q = new ECPoint
            {
                X = Base64Url.DecodeFromChars(root.GetProperty("x").GetString()),
                Y = Base64Url.DecodeFromChars(root.GetProperty("y").GetString()),
            },
        };
        parameters.Validate();
        using var key = ECDiffieHellman.Create(parameters);
        return key.PublicKey;
    }

    /// <summary>
    /// HKDF-SHA256 of the ECDH shared point's X coordinate (at the curve's field length), with the identity's salt
    /// and no info, to 16 bytes
    /// </summary>
    public static byte[] ExchangeSecret(ECDiffieHellman ownKey, string otherPublicKeyJwkBase64Url, byte[] salt)
    {
        if (salt.Length < 16)
        {
            throw new CryptographicException("The salt must be at least 16 bytes");
        }

        using var otherPublicKey = ReadPublicKeyJwkBase64Url(otherPublicKeyJwkBase64Url);
        var sharedX = ownKey.DeriveRawSecretAgreement(otherPublicKey);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedX, ExchangeSecretSize, salt, info: []);
    }

    /// <summary>The token request's <c>secret_digest</c></summary>
    public static string SecretDigest(byte[] exchangeSecret) => Convert.ToBase64String(SHA256.HashData(exchangeSecret));

    /// <summary>
    /// Opens a token field sealed with AES-GCM. Anything else is refused: this client asks for aes-gcm, and an
    /// identity answering aes-cbc would be a downgrade.
    /// </summary>
    public static byte[] OpenGcm(string? cipher, byte[] key, byte[] iv, byte[] cipherTextWithTag)
    {
        if (cipher != CipherAesGcm)
        {
            throw new CryptographicException($"The token is sealed with '{cipher ?? CipherAesCbc}', not {CipherAesGcm}");
        }
        if (iv.Length < GcmNonceSize || cipherTextWithTag.Length < GcmTagSize)
        {
            throw new CryptographicException("The sealed token is too short");
        }

        var cipherTextLength = cipherTextWithTag.Length - GcmTagSize;
        var plain = new byte[cipherTextLength];
        using var gcm = new AesGcm(key, GcmTagSize);
        gcm.Decrypt(iv.AsSpan(0, GcmNonceSize), cipherTextWithTag.AsSpan(0, cipherTextLength),
            cipherTextWithTag.AsSpan(cipherTextLength), plain);
        return plain;
    }

    /// <summary>
    /// Seals as odin-core's <c>AesGcm.Encrypt</c> does: a random 16-byte IV whose first 12 bytes are the nonce, the
    /// tag appended. The identity's side, for tests.
    /// </summary>
    public static (byte[] Iv, byte[] CipherTextWithTag) SealGcm(byte[] key, byte[] plain)
    {
        var iv = RandomNumberGenerator.GetBytes(16);
        var result = new byte[plain.Length + GcmTagSize];
        using var gcm = new AesGcm(key, GcmTagSize);
        gcm.Encrypt(iv.AsSpan(0, GcmNonceSize), plain, result.AsSpan(0, plain.Length), result.AsSpan(plain.Length));
        return (iv, result);
    }
}
