using System.IO.Compression;
using System.Text;
using Moonfin.Games;
using Xunit;

namespace Moonfin.Games.Tests;

public sealed class DiscSetTests : IDisposable
{
    // A solid .7z holding Game/Game.cue and Game/Game.bin (5000 bytes of (i * 7) % 251), made with
    // `7z a -t7z -ms=on -mx=9`.
    private const string SolidSevenZip =
        "N3q8ryccAASrerBuvAEAAAAAAAAiAAAAAAAAAGfJcQjgE8oBP10AAAIPVwJoxnjO2A+Q5uu23R9wYrAhJxT5sZWKWGAheiys53eY30WGsYjf+PcFLdU18Xr6gK275dGyusOKquQRMXvha85O/6E4K7mbKsZwcKVjLJ/wEA6+jQPWDL5ku6/IRQRTlzG5tazt7BY7eTC8IsZUz+leMEkFnctdvwskckmbeoGIk02faUM/D+XVNfqWMbNi1i1IeuMoILkWrEaH0H6l5kBcS8jq5ObpLhOjFSpsRxw2WFf2A5fIBGU87Ye7o17lyykAYx+lcYH3s+OuJywpjeLNzCtC63vH/zoa0KiT5dfC9Ocu0EzJipEEYmhVCrUo+VP3N/rfbJUDANMUoX0M0XeQDR6hk96rwW1xHHSoH56IF6RtTS3kQdnQnCllbbSYwAmcfaujGTHIkVCa/OUaAG4fAt2e9ZCmAjR+QAAAAIEzB64P1Tlkltck0/6zcBaxUe6SRMCg7LPGKw/pGFGps9SCPSVfFyk11AN+MuU3VWda7TuGLrWvtY5J7RNwSGjswj24h47jzeanTwijp3HMiNfdg6hqj+P83mm0mkCjpB1nZrTQVwIePTVXqPNakeUAAAAXBoFHAQl1AAcLAQABIwMBAQVdABAAAAyAogoBg9EHQAAA";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"discset-{Guid.NewGuid():N}");

    public DiscSetTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("FILE \"Crash Bandicoot (USA).bin\" BINARY", "Crash Bandicoot (USA).bin")]
    [InlineData("  file \"Game (Track 2).bin\" BINARY", "Game (Track 2).bin")]
    [InlineData("FILE game.bin BINARY", "game.bin")]
    [InlineData("FILE game.bin", "game.bin")]
    [InlineData("  TRACK 01 MODE2/2352", null)]
    [InlineData("FILE \"\" BINARY", null)]
    [InlineData("REM FILE \"x.bin\" BINARY", null)]
    public void TrackFileName_ReadsQuotedAndBareNames(string line, string? expected) =>
        Assert.Equal(expected, DiscSet.TrackFileName(line));

    [Fact]
    public void CueSetFiles_ListsTheSheetThenEachTrackOnce_MatchingNamesWithoutCase()
    {
        var cue = Write("Game.cue",
            "FILE \"GAME (Track 1).BIN\" BINARY\n  TRACK 01 MODE2/2352\n" +
            "FILE \"Game (Track 2).bin\" BINARY\n  TRACK 02 AUDIO\n" +
            "FILE \"Game (Track 2).bin\" BINARY\n  TRACK 03 AUDIO\n");
        var track1 = Write("Game (Track 1).bin", "one");
        var track2 = Write("Game (Track 2).bin", "two");

        Assert.Equal(new[] { cue, track1, track2 }, DiscSet.CueSetFiles(cue));
    }

    [Theory]
    [InlineData("FILE \"Missing.bin\" BINARY\n")]
    [InlineData("FILE \"../Outside.bin\" BINARY\n")]
    [InlineData("FILE \"sub\\Game.bin\" BINARY\n")]
    [InlineData("REM nothing listed\n")]
    public void CueSetFiles_RefusesASheetWithAMissingOrOutsideTrackOrNone(string sheet)
    {
        Directory.CreateDirectory(Path.Combine(_root, "disc", "sub"));
        File.WriteAllText(Path.Combine(_root, "disc", "sub", "Game.bin"), "x");
        File.WriteAllText(Path.Combine(_root, "Outside.bin"), "x");
        var cue = Path.Combine(_root, "disc", "Game.cue");
        File.WriteAllText(cue, sheet);

        Assert.Null(DiscSet.CueSetFiles(cue));
        Assert.Null(DiscSet.ForCueSheet(cue));
    }

    [Fact]
    public void ForCueSheet_WritesExactlyItsLength_AsAStoredZipEveryReaderAccepts()
    {
        var cue = Write("Game.cue", "FILE \"Game.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n");
        Write("Game.bin", "123456789");

        var discSet = DiscSet.ForCueSheet(cue)!;
        var bytes = WriteToForwardOnlyStream(discSet);

        Assert.Equal(discSet.Length, bytes.Length);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.Equal(new[] { "Game.cue", "Game.bin" }, zip.Entries.Select(e => e.FullName));
        var bin = zip.GetEntry("Game.bin")!;
        Assert.Equal(bin.Length, bin.CompressedLength);
        // The standard check value for CRC-32 over "123456789".
        Assert.Equal(0xCBF43926u, bin.Crc32);
        Assert.Equal("123456789", ReadText(bin));
        Assert.StartsWith("FILE \"Game.bin\"", ReadText(zip.GetEntry("Game.cue")!));
    }

    [Fact]
    public void ForSevenZip_FlattensEveryFile_AndWritesExactlyItsLength()
    {
        var archive = Path.Combine(_root, "Game.7z");
        File.WriteAllBytes(archive, Convert.FromBase64String(SolidSevenZip));

        var discSet = DiscSet.ForSevenZip(archive)!;
        var bytes = WriteToForwardOnlyStream(discSet);

        Assert.Equal(discSet.Length, bytes.Length);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.Equal(new[] { "Game.bin", "Game.cue" }, zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal));
        using var bin = new MemoryStream();
        zip.GetEntry("Game.bin")!.Open().CopyTo(bin);
        Assert.Equal(Enumerable.Range(0, 5000).Select(i => (byte)(i * 7 % 251)), bin.ToArray());
        Assert.StartsWith("FILE \"Game.bin\" BINARY", ReadText(zip.GetEntry("Game.cue")!));
    }

    [Fact]
    public void ForSevenZip_IsNullForAnUnreadableArchive()
    {
        var archive = Path.Combine(_root, "Broken.7z");
        File.WriteAllText(archive, "not a 7z");

        Assert.Null(DiscSet.ForSevenZip(archive));
    }

    [Fact]
    public void WriteTo_FailsRatherThanSendingLessThanItPromised()
    {
        var cue = Write("Game.cue", "FILE \"Game.bin\" BINARY\n");
        var bin = Write("Game.bin", "123456789");
        var discSet = DiscSet.ForCueSheet(cue)!;
        File.WriteAllText(bin, "1234");

        Assert.Throws<EndOfStreamException>(() => discSet.WriteTo(new MemoryStream()));
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static string ReadText(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // A response body can't seek, so the zip must come out right without it.
    private static byte[] WriteToForwardOnlyStream(DiscSet discSet)
    {
        var buffer = new MemoryStream();
        discSet.WriteTo(new ForwardOnlyStream(buffer));
        return buffer.ToArray();
    }

    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
