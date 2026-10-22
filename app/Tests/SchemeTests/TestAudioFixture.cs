using System.IO;

/// <summary>自行生成的静音 PTADPCM WEM，不依赖游戏或本机研究音频。</summary>
internal sealed class TestAudioFixture : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "qxbgm-audio-fixture-" + Guid.NewGuid().ToString("N"));
    public string WemPath => Path.Combine(_directory, "silence.wem");

    public TestAudioFixture()
    {
        Directory.CreateDirectory(_directory);
        const int payloadLength = 72 * 64;
        using var writer = new BinaryWriter(File.Create(WemPath));
        writer.Write("RIFF"u8); writer.Write(56 + payloadLength); writer.Write("WAVEfmt "u8);
        writer.Write(24); writer.Write((ushort)0x8311); writer.Write((ushort)2); writer.Write(48000);
        writer.Write(54000); writer.Write((ushort)72); writer.Write((ushort)4); writer.Write((ushort)6);
        writer.Write(new byte[] { 0, 0, 2, 0x31, 0, 0 });
        writer.Write("JUNK"u8); writer.Write(4); writer.Write(0);
        writer.Write("data"u8); writer.Write(payloadLength); writer.Write(new byte[payloadLength]);
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
