using System.IO;

/// <summary>自行生成的静音 PTADPCM WEM，不依赖游戏或本机研究音频。</summary>
internal sealed class TestAudioFixture : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "qxbgm-audio-fixture-" + Guid.NewGuid().ToString("N"));
    public string WemPath => Path.Combine(_directory, "silence.wem");

    public TestAudioFixture()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(WemPath, Create());
    }

    internal static byte[] Create(int channels = 2, int frames = 64)
    {
        int payloadLength = channels * 36 * frames;
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        writer.Write("RIFF"u8); writer.Write(56 + payloadLength); writer.Write("WAVEfmt "u8);
        writer.Write(24); writer.Write((ushort)0x8311); writer.Write((ushort)channels); writer.Write(48000);
        writer.Write(27000 * channels); writer.Write((ushort)(36 * channels)); writer.Write((ushort)4); writer.Write((ushort)6);
        writer.Write((ushort)0); writer.Write(channels == 1 ? 0x4101u : 0x3102u);
        writer.Write("JUNK"u8); writer.Write(4); writer.Write(0);
        writer.Write("data"u8); writer.Write(payloadLength); writer.Write(new byte[payloadLength]);
        return buffer.ToArray();
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
