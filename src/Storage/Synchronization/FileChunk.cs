using System.Security.Cryptography;
using K4os.Compression.LZ4;

using HashAlgorithm = DiscordFS.Helpers.HashAlgorithm;

namespace DiscordFS.Storage.Synchronization;

public class FileChunk
{
    public const byte Version = 0x01;

    /*
     * Layout:
     *
     * HEADER
     * ------
     * [Version]          - 1 byte
     * [Index]            - 4 bytes
     * [IsCompressed]     - 1 byte
     * [IsEncrypted]      - 1 byte
     *                    
     * BODY               
     * ----               
     * [Body Full Size]   - 4 bytes
     * [Body Comp. Size]  - 4 bytes
     * [Body]             - [Body Size] bytes
     * [Hash Algorithm]   - 1 byte
     * [Hash]             - n bytes (md5: 16 bytes)
     */

    public byte[] Data { get; set; }

    public byte[] Hash { get; set; }

    public HashAlgorithm HashAlgorithm { get; set; }

    public bool IsCompressed { get; set; }

    public bool IsEncrypted { get; set; }

    public int Index { get; set; }

    public FileChunk(bool useCompression, bool isEncrypted)
    {
        IsCompressed = useCompression;
        IsEncrypted = isEncrypted;
    }

    public FileChunk() { }

    public static FileChunk Deserialize(byte[] data, byte[] encryptionKey = null)
    {
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);

        ms.Seek(offset: 0, SeekOrigin.Begin);

        var chunk = new FileChunk();

        var version = br.ReadByte();
        if (version != Version)
        {
            throw new Exception(message: "File chunk version is not supported");
        }

        chunk.Index = br.ReadInt32();
        chunk.IsCompressed = br.ReadBoolean();
        chunk.IsEncrypted = br.ReadBoolean();

        var fullSize = br.ReadInt32();
        var bodySize = br.ReadInt32();
        var body = br.ReadBytes(bodySize);

        if (chunk.IsEncrypted)
        {
            body = Decrypt(body, encryptionKey);
        }

        if (chunk.IsCompressed)
        {
            body = Decompress(body, fullSize);
        }

        chunk.Data = body;
        chunk.HashAlgorithm = (HashAlgorithm)br.ReadByte();

        byte[] expectedHash;
        switch (chunk.HashAlgorithm)
        {
            case HashAlgorithm.Md5:
                chunk.Hash = MD5.HashData(body);
                expectedHash = br.ReadBytes(count: 16);
                break;

            case HashAlgorithm.Sha256:
                chunk.Hash = SHA256.HashData(body);
                expectedHash = br.ReadBytes(count: 16);
                break;

            default:
                throw new Exception($"Unknown hash algorithm ID: {(int)chunk.HashAlgorithm}");
        }

        if (!expectedHash.SequenceEqual(chunk.Hash))
        {
            throw new Exception(message: "Chunk corrupted: invalid hash");
        }

        return chunk;
    }

    public byte[] Serialize(byte[] encryptionKey = null)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        IsEncrypted = encryptionKey != null;

        bw.Write(Version);
        bw.Write(Index);
        bw.Write(IsCompressed);
        bw.Write(IsEncrypted);

        var hash = MD5.HashData(Data);
        var originalSize = Data.Length;

        var body = IsCompressed
            ? Compress(Data)
            : Data;

        if (IsEncrypted)
        {
            body = Encrypt(body, encryptionKey);
        }

        bw.Write(originalSize);
        bw.Write(body.Length);
        bw.Write(body);

        bw.Write((byte)HashAlgorithm);
        bw.Write(hash);

        bw.Flush();

        ms.Seek(offset: 0, SeekOrigin.Begin);
        return ms.ToArray();
    }

    private static byte[] Compress(byte[] data)
    {
        var target = new byte[LZ4Codec.MaximumOutputSize(data.Length)];
        var count = LZ4Codec.Encode(data, target, LZ4Level.L06_HC);
        var result = new byte[count];

        Buffer.BlockCopy(target, srcOffset: 0, result, dstOffset: 0, count);
        return result;
    }

    private static byte[] Decompress(byte[] data, int decompressedSize)
    {
        var target = new byte[decompressedSize];
        var count = LZ4Codec.Decode(data, target);
        var result = new byte[count];

        Buffer.BlockCopy(target, srcOffset: 0, result, dstOffset: 0, count);
        return result;
    }

    private static byte[] Encrypt(byte[] body, byte[] encryptionKey)
    {
        using var aes = Aes.Create();
        aes.Key = encryptionKey;
        aes.Mode = CipherMode.CBC;
        aes.GenerateIV();
        var iv = aes.IV;

        using var encryptor = aes.CreateEncryptor();
        using var memoryStream = new MemoryStream();
        using (var cryptoStream = new CryptoStream(memoryStream, encryptor, CryptoStreamMode.Write))
            cryptoStream.Write(body, offset: 0, body.Length);

        var encryptedBody = memoryStream.ToArray();
        var encryptedBodyWithIv = new byte[iv.Length + encryptedBody.Length];
        Buffer.BlockCopy(iv, srcOffset: 0, encryptedBodyWithIv, dstOffset: 0, iv.Length);
        Buffer.BlockCopy(encryptedBody, srcOffset: 0, encryptedBodyWithIv, iv.Length, encryptedBody.Length);
        return encryptedBodyWithIv;
    }

    private static byte[] Decrypt(byte[] body, byte[] encryptionKey)
    {
        using var aes = Aes.Create();
        aes.Key = encryptionKey;
        aes.Mode = CipherMode.CBC;


        var iv = new byte[aes.BlockSize / 8];
        Buffer.BlockCopy(body, srcOffset: 0, iv, dstOffset: 0, iv.Length);
        aes.IV = iv;
        var encryptedBody = new byte[body.Length - iv.Length];
        Buffer.BlockCopy(body, iv.Length, encryptedBody, dstOffset: 0, encryptedBody.Length);

        using var decryptor = aes.CreateDecryptor();
        using var memoryStream = new MemoryStream();
        using (var cryptoStream = new CryptoStream(memoryStream, decryptor, CryptoStreamMode.Write))
            cryptoStream.Write(encryptedBody, offset: 0, encryptedBody.Length);

        var decryptedBody = memoryStream.ToArray();
        return decryptedBody;
    }

    public static FileChunk CreateChunk(
        int chunkIndex,
        byte[] data,
        int offset,
        int length,
        bool useCompression,
        bool isEncrypted)
    {
        var newData = new byte[length];
        Buffer.BlockCopy(data, srcOffset: 0, newData, offset, length);

        return new FileChunk(useCompression, isEncrypted)
        {
            Data = newData,
            Index = chunkIndex,
            HashAlgorithm = HashAlgorithm.Md5,
            Hash = MD5.HashData(data)
    };
    }
}