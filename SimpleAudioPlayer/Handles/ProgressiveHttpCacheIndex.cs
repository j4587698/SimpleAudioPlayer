using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SimpleAudioPlayer.Handles;

internal sealed class ProgressiveHttpCacheIndex
{
    private const uint Magic = 0x31505853; // SXP1
    private const ushort Version = 1;
    private const int ChecksumLength = 32;
    private const int MaxRangeCount = 1_000_000;

    public required ulong UrlHash { get; init; }
    public required long TotalBytes { get; init; }
    public required ulong ETagHash { get; init; }
    public required long LastModifiedUtcTicks { get; init; }
    public required IReadOnlyList<CachedByteRange> Ranges { get; init; }

    public static ProgressiveHttpCacheIndex Create(
        string url,
        long totalBytes,
        string? eTag,
        DateTimeOffset? lastModified,
        IReadOnlyList<CachedByteRange> ranges)
    {
        return new ProgressiveHttpCacheIndex
        {
            UrlHash = HashValue(url),
            TotalBytes = totalBytes,
            ETagHash = HashValue(eTag),
            LastModifiedUtcTicks = lastModified?.UtcTicks ?? 0,
            Ranges = ranges.ToArray()
        };
    }

    public bool Matches(string url, long totalBytes, string? eTag, DateTimeOffset? lastModified)
    {
        if (UrlHash != HashValue(url))
        {
            return false;
        }

        if (TotalBytes > 0 && totalBytes > 0 && TotalBytes != totalBytes)
        {
            return false;
        }

        var currentETagHash = HashValue(eTag);
        if (ETagHash != 0 && currentETagHash != 0 && ETagHash != currentETagHash)
        {
            return false;
        }

        var currentLastModifiedTicks = lastModified?.UtcTicks ?? 0;
        return LastModifiedUtcTicks == 0
            || currentLastModifiedTicks == 0
            || LastModifiedUtcTicks == currentLastModifiedTicks;
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write((ushort)0);
            writer.Write(UrlHash);
            writer.Write(TotalBytes);
            writer.Write(ETagHash);
            writer.Write(LastModifiedUtcTicks);
            writer.Write(Ranges.Count);

            foreach (var range in Ranges)
            {
                writer.Write(range.Start);
                writer.Write(range.Length);
            }
        }

        var payload = body.ToArray();
        var checksum = SHA256.HashData(payload);
        var tempPath = path + ".tmp";

        using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            output.Write(payload, 0, payload.Length);
            output.Write(checksum, 0, checksum.Length);
            output.Flush(true);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    public static ProgressiveHttpCacheIndex? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length <= ChecksumLength)
            {
                return null;
            }

            var payloadLength = bytes.Length - ChecksumLength;
            var checksum = SHA256.HashData(bytes.AsSpan(0, payloadLength));
            if (!CryptographicOperations.FixedTimeEquals(
                    checksum,
                    bytes.AsSpan(payloadLength, ChecksumLength)))
            {
                return null;
            }

            using var body = new MemoryStream(bytes, 0, payloadLength, writable: false);
            using var reader = new BinaryReader(body, Encoding.UTF8, leaveOpen: false);
            if (reader.ReadUInt32() != Magic)
            {
                return null;
            }

            if (reader.ReadUInt16() != Version)
            {
                return null;
            }

            reader.ReadUInt16(); // reserved
            var urlHash = reader.ReadUInt64();
            var totalBytes = reader.ReadInt64();
            var eTagHash = reader.ReadUInt64();
            var lastModifiedTicks = reader.ReadInt64();
            var rangeCount = reader.ReadInt32();
            if (rangeCount < 0 || rangeCount > MaxRangeCount)
            {
                return null;
            }

            var ranges = new List<CachedByteRange>(rangeCount);
            for (var i = 0; i < rangeCount; i++)
            {
                var start = reader.ReadInt64();
                var length = reader.ReadInt64();
                if (start < 0 || length <= 0 || start > long.MaxValue - length)
                {
                    return null;
                }

                ranges.Add(new CachedByteRange(start, length));
            }

            if (body.Position != payloadLength)
            {
                return null;
            }

            return new ProgressiveHttpCacheIndex
            {
                UrlHash = urlHash,
                TotalBytes = totalBytes,
                ETagHash = eTagHash,
                LastModifiedUtcTicks = lastModifiedTicks,
                Ranges = ranges
            };
        }
        catch
        {
            return null;
        }
    }

    public static ulong HashValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }
}
