using System.Security.Cryptography;
using System.Text;

namespace Raven.Hosting.Tests;

/// <summary>
/// YouAuthCrypto against odin-core's own: the vectors are what odin-core's Odin.Core.Cryptography
/// (EccFullKeyData.PublicKeyJwkBase64Url, GetEcdhSharedSecret, AesGcm.Encrypt) produced for two fixed P-384 keys,
/// a fixed salt and a fixed IV. If these hold, a real identity and this service derive the same secret and open
/// each other's tokens.
/// </summary>
public class YouAuthCryptoTests
{
    // The client's (this service's) private scalar
    private const string ClientD = "DSzofYb1X8+rdwoEewkNojJw+iBoMt/qfgyUb/9FH4Ga3SQjdL5VGw1jGO1sfUHY";

    private const string ClientPublicJwkBase64Url =
        "eyJrdHkiOiJFQyIsImNydiI6IlAtMzg0IiwieCI6IjJyQ00zMmpOLU9OYk95Z3VhdzF2SmZiNGhFX1liS0k1aWJ6SFZ5X0hwOHVjZkpYS2s1dU92Rk9qeGthRWhxTGwiLCJ5IjoiS1NOVUVyOUVTcWl5UzZuSjlyckR3R1M2V1drSTNBWHNYbGhibXVZd2pYTzhIelpURkpXcUFUUWRJc3NPNmZ1biJ9";

    private const string IdentityPublicJwkBase64Url =
        "eyJrdHkiOiJFQyIsImNydiI6IlAtMzg0IiwieCI6IktMWXl0bXQtU1BCdWJFaDYxa01Bb2xMemo1Sm1qc21oekZ1eEFiYUZzdUp2WlAwYXNtVTRrZEhCTXVpVHdyWlEiLCJ5IjoicHMzb1VLaFdGaUtERnNfNzB6Y056N21QU05LY1FpNjBUbGdNc3Ixc0dkNEJMT1c1X3ZLZDc1RDFJemRzLXJPMSJ9";

    private const string Salt = "AwoRGB8mLTQ7QklQV15lbA==";
    private const string ExchangeSecret = "cxNld9X19vNmd/rjEf+cwg==";
    private const string SecretDigest = "39wZEzP1bbTh0JKGExL51ObDYbZwtrYvrbRG+2PzEOM=";
    private const string Iv = "yMfGxcTDwsHAv769vLu6uQ==";
    private const string Sealed = "AMcKBjgoMxznamfYbxO9xIfBm0vg+gF+dCiN8cp1YBAqtGSVVQ==";
    private const string Plain = "odin-core sealed this";

    private static ECDiffieHellman ClientKey() => ECDiffieHellman.Create(new ECParameters
    {
        Curve = ECCurve.NamedCurves.nistP384,
        D = Convert.FromBase64String(ClientD),
    });

    [Test]
    public void ThePublicKeyIsTheJwkOdinCoreWrites()
    {
        using var key = ClientKey();
        Assert.That(YouAuthCrypto.PublicKeyJwkBase64Url(key), Is.EqualTo(ClientPublicJwkBase64Url));
    }

    [Test]
    public void TheExchangeSecretAndItsDigestAreOdinCores()
    {
        using var key = ClientKey();
        var secret = YouAuthCrypto.ExchangeSecret(key, IdentityPublicJwkBase64Url, Convert.FromBase64String(Salt));

        Assert.That(Convert.ToBase64String(secret), Is.EqualTo(ExchangeSecret));
        Assert.That(YouAuthCrypto.SecretDigest(secret), Is.EqualTo(SecretDigest));
    }

    [Test]
    public void ATokenFieldOdinCoreSealedOpens()
    {
        var plain = YouAuthCrypto.OpenGcm(YouAuthCrypto.CipherAesGcm, Convert.FromBase64String(ExchangeSecret),
            Convert.FromBase64String(Iv), Convert.FromBase64String(Sealed));

        Assert.That(Encoding.UTF8.GetString(plain), Is.EqualTo(Plain));
    }

    [Test]
    public void AChangedByteOrAnotherCipherIsRefused()
    {
        var key = Convert.FromBase64String(ExchangeSecret);
        var iv = Convert.FromBase64String(Iv);
        var tampered = Convert.FromBase64String(Sealed);
        tampered[3] ^= 1;

        Assert.Throws(Is.InstanceOf<CryptographicException>(), () => YouAuthCrypto.OpenGcm(YouAuthCrypto.CipherAesGcm, key, iv, tampered));
        Assert.Throws<CryptographicException>(() =>
            YouAuthCrypto.OpenGcm(YouAuthCrypto.CipherAesCbc, key, iv, Convert.FromBase64String(Sealed)), "a downgrade");
        Assert.Throws<CryptographicException>(() =>
            YouAuthCrypto.OpenGcm(null, key, iv, Convert.FromBase64String(Sealed)), "no cipher means aes-cbc");
    }
}
