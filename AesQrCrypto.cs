using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CashPlace;

public class AesQrCrypto
{
    private const string SecretKey = "MySuperSecretKey2024ForQRCodes!!";

    public static string Encrypt(string plainText)
    {
        byte[] keyBytes = Encoding.UTF8.GetBytes(SecretKey);
        byte[] iv = new byte[16];

        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(iv);
        }

        using (var aes = Aes.Create())
        {
            aes.Key = keyBytes;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using (var encryptor = aes.CreateEncryptor())
            using (var ms = new MemoryStream())
            {
                ms.Write(iv, 0, iv.Length);
                using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                using (var sw = new StreamWriter(cs))
                {
                    sw.Write(plainText);
                }
                return Convert.ToBase64String(ms.ToArray());
            }
        }
    }

    public static string Decrypt(string base64CipherText)
    {
        byte[] fullBytes = Convert.FromBase64String(base64CipherText);
        byte[] keyBytes = Encoding.UTF8.GetBytes(SecretKey);

        byte[] iv = new byte[16];
        Buffer.BlockCopy(fullBytes, 0, iv, 0, 16);

        byte[] cipherBytes = new byte[fullBytes.Length - 16];
        Buffer.BlockCopy(fullBytes, 16, cipherBytes, 0, cipherBytes.Length);

        using (var aes = Aes.Create())
        {
            aes.Key = keyBytes;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using (var decryptor = aes.CreateDecryptor())
            using (var ms = new MemoryStream(cipherBytes))
            using (var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
            using (var sr = new StreamReader(cs))
            {
                return sr.ReadToEnd();
            }
        }
    }
}

// Класс для JSON тоже можно положить сюда
public class StoreConfig
{
    [System.Text.Json.Serialization.JsonPropertyName("guid")]
    public string Guid { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("nick")]
    public string Nick { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("exp")]
    public long Exp { get; set; }
}