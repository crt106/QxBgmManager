using System.Buffers.Binary;
using System.IO;
using System.Text;

internal sealed class SyntheticPckFixture : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "qxbgm-safe-fixture-" + Guid.NewGuid().ToString("N"));
    public string PckPath => Path.Combine(DirectoryPath, "BeydMusic0.pck");
    public byte[] Original { get; }
    public const int Capacity = 65536;
    public SyntheticPckFixture(byte[]? payload = null)
    {
        Directory.CreateDirectory(DirectoryPath);
        Original = new byte[128 + Capacity * 2];
        new Random(46).NextBytes(Original);
        Encoding.ASCII.GetBytes("AKPK").CopyTo(Original, 0);
        Original.AsSpan(12, 16).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(Original.AsSpan(28), 2);
        for (int i = 0; i < 2; i++)
        {
            var lut = Original.AsSpan(32 + 20 * i, 20);
            BinaryPrimitives.WriteUInt32LittleEndian(lut, (uint)(123 + i));
            BinaryPrimitives.WriteUInt32LittleEndian(lut[4..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(lut[8..], payload == null ? Capacity : (uint)payload.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(lut[12..], (ulong)(128 + i * Capacity));
            payload?.CopyTo(Original, 128 + i * Capacity);
        }
        File.WriteAllBytes(PckPath, Original);
    }
    public void Dispose() => Directory.Delete(DirectoryPath, true);
}
