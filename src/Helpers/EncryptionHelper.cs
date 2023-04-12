using System.Security.Cryptography;

namespace DiscordFS.Helpers;

public static class EncryptionHelper
{
    public static byte[] Encrypt(byte[] data, byte[] encryptionKey)
    {
        using var aes = Aes.Create();
        aes.Key = encryptionKey;
        aes.Mode = CipherMode.CBC;
        aes.GenerateIV();
        var iv = aes.IV;

        using var encryptor = aes.CreateEncryptor();
        using var memoryStream = new MemoryStream();
        using (var cryptoStream = new CryptoStream(memoryStream, encryptor, CryptoStreamMode.Write))
        {
            cryptoStream.Write(data, offset: 0, data.Length);
        }

        var encryptedBody = memoryStream.ToArray();
        var encryptedBodyWithIv = new byte[iv.Length + encryptedBody.Length];
        Buffer.BlockCopy(iv, srcOffset: 0, encryptedBodyWithIv, dstOffset: 0, iv.Length);
        Buffer.BlockCopy(encryptedBody, srcOffset: 0, encryptedBodyWithIv, iv.Length, encryptedBody.Length);
        return encryptedBodyWithIv;
    }

    public static byte[] Decrypt(byte[] data, byte[] encryptionKey)
    {
        using var aes = Aes.Create();
        aes.Key = encryptionKey;
        aes.Mode = CipherMode.CBC;

        var iv = new byte[aes.BlockSize / 8];
        Buffer.BlockCopy(data, srcOffset: 0, iv, dstOffset: 0, iv.Length);
        aes.IV = iv;
        var encryptedBody = new byte[data.Length - iv.Length];
        Buffer.BlockCopy(data, iv.Length, encryptedBody, dstOffset: 0, encryptedBody.Length);

        using var decryptor = aes.CreateDecryptor();
        using var memoryStream = new MemoryStream();
        using (var cryptoStream = new CryptoStream(memoryStream, decryptor, CryptoStreamMode.Write))
        {
            cryptoStream.Write(encryptedBody, offset: 0, encryptedBody.Length);
        }

        var decryptedBody = memoryStream.ToArray();
        return decryptedBody;
    }
}