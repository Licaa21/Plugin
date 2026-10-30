using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SharpCompress.Archives;

namespace Moonfin.Games;

/// <summary>
/// A PlayStation disc is often a .cue sheet plus the .bin tracks it lists, and the core needs
/// every one of them. Both players already unpack a zip, so a disc set that isn't a zip on disk
/// goes out as one uncompressed zip, written while it streams instead of unpacked into memory.
/// Its exact length is known up front, so it can be sent with a Content-Length.
/// </summary>
public sealed class DiscSet
{
    // A cue sheet is a few lines of text, so anything bigger isn't one.
    private const long MaxCueBytes = 64 * 1024;

    private readonly Action<StoredZipWriter> _write;

    private DiscSet(long length, Action<StoredZipWriter> write)
    {
        Length = length;
        _write = write;
    }

    /// <summary>The exact number of bytes <see cref="WriteTo"/> writes.</summary>
    public long Length { get; }

    public void WriteTo(Stream output)
    {
        var writer = new StoredZipWriter(output);
        _write(writer);
        writer.Finish();
    }

    /// <summary>A cue sheet and its tracks. Null when <see cref="CueSetFiles"/> finds no complete set.</summary>
    public static DiscSet? ForCueSheet(string cuePath)
    {
        var files = CueSetFiles(cuePath);
        if (files == null)
        {
            return null;
        }

        var entries = files.Select(file => (Name: Path.GetFileName(file), Size: new FileInfo(file).Length)).ToList();
        var length = StoredZipWriter.Length(entries);
        return length == null ? null : new DiscSet(length.Value, writer =>
        {
            for (var i = 0; i < files.Count; i++)
            {
                using var source = File.OpenRead(files[i]);
                writer.Add(entries[i].Name, entries[i].Size, source);
            }
        });
    }

    /// <summary>Everything in a .7z. Null when the archive can't be read.</summary>
    public static DiscSet? ForSevenZip(string archivePath)
    {
        // SharpCompress loads when ReadSevenZip is first called, so a copy that fails to load is
        // caught here too.
        try
        {
            return ReadSevenZip(archivePath);
        }
        catch
        {
            return null;
        }
    }

    // The archive is read once from start to end when written, since a solid .7z can't be
    // unpacked one file at a time without starting over.
    private static DiscSet? ReadSevenZip(string archivePath)
    {
        List<(string Name, long Size)> entries;
        using (var archive = ArchiveFactory.Open(archivePath))
        {
            entries = archive.Entries
                .Where(entry => !entry.IsDirectory)
                .Select(entry => (Name: EntryName(entry.Key), entry.Size))
                .Where(entry => entry.Name.Length > 0)
                .ToList();
        }

        var length = StoredZipWriter.Length(entries);
        return length == null ? null : new DiscSet(length.Value, writer =>
        {
            using var archive = ArchiveFactory.Open(archivePath);
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                var name = EntryName(reader.Entry.Key);
                if (reader.Entry.IsDirectory || name.Length == 0)
                {
                    continue;
                }

                using var source = reader.OpenEntryStream();
                writer.Add(name, reader.Entry.Size, source);
            }
        });
    }

    /// <summary>
    /// The cue sheet followed by the track files it lists, all from the sheet's own folder. Null
    /// when the sheet lists no tracks, or a track is missing or points outside that folder.
    /// </summary>
    public static IReadOnlyList<string>? CueSetFiles(string cuePath)
    {
        var sheet = new FileInfo(cuePath);
        if (!sheet.Exists || sheet.Length > MaxCueBytes || sheet.DirectoryName == null)
        {
            return null;
        }

        var files = new List<string> { sheet.FullName };
        foreach (var line in File.ReadLines(sheet.FullName))
        {
            var name = TrackFileName(line);
            if (name == null)
            {
                continue;
            }

            var track = FindInFolder(sheet.DirectoryName, name);
            if (track == null)
            {
                return null;
            }

            if (!files.Contains(track, StringComparer.Ordinal))
            {
                files.Add(track);
            }
        }

        return files.Count > 1 ? files : null;
    }

    /// <summary>The file a cue sheet line names, from <c>FILE "name.bin" BINARY</c> or an unquoted name.</summary>
    public static string? TrackFileName(string line)
    {
        var text = line.Trim();
        if (!text.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        text = text.Substring(5).Trim();
        if (text.StartsWith("\"", StringComparison.Ordinal))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text.Substring(1, end - 1) : null;
        }

        // An unquoted name can't hold a space, so the last word is the file type.
        var space = text.LastIndexOf(' ');
        var name = space > 0 ? text.Substring(0, space).Trim() : text;
        return name.Length > 0 ? name : null;
    }

    // Only the last segment of an archive key names the file. Backslashes turn up in archives
    // written on Windows.
    private static string EntryName(string? key) => Path.GetFileName((key ?? string.Empty).Replace('\\', '/'));

    // A track has to be a bare name in the sheet's own folder. It's matched without case, since
    // sheets written on Windows often disagree with the disk about it.
    private static string? FindInFolder(string folder, string name)
    {
        if (name.Contains('\\') || Path.GetFileName(name) != name || name is "." or "..")
        {
            return null;
        }

        // Listed rather than probed with File.Exists, which on a case-insensitive disk says yes to
        // the sheet's spelling and would put that spelling, not the file's own name, in the zip.
        var files = Directory.EnumerateFiles(folder).ToList();
        return files.FirstOrDefault(file => string.Equals(Path.GetFileName(file), name, StringComparison.Ordinal))
            ?? files.FirstOrDefault(file => string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase));
    }

    // Writes an uncompressed zip in one pass. Each entry's CRC follows its data in a descriptor,
    // and readers take it from there or from the central directory. Zip64 is never needed for a
    // disc, so a set that would need it is refused.
    private sealed class StoredZipWriter
    {
        private const ushort Version = 20;
        private const ushort Flags = 0x0008 | 0x0800; // data descriptor, UTF-8 names
        private const ushort DosDate = (0 << 9) | (1 << 5) | 1; // 1980-01-01
        private const int LocalHeaderLength = 30;
        private const int DescriptorLength = 16;
        private const int CentralHeaderLength = 46;
        private const int EndLength = 22;

        private static readonly uint[] CrcTable = BuildCrcTable();

        private readonly Stream _output;
        private readonly byte[] _buffer = new byte[1 << 20];
        private readonly List<(byte[] Name, uint Crc, uint Size, uint Offset)> _central = new();
        private long _position;

        public StoredZipWriter(Stream output)
        {
            _output = output;
        }

        public static long? Length(IReadOnlyCollection<(string Name, long Size)> entries)
        {
            long total = EndLength;
            foreach (var (name, size) in entries)
            {
                var nameLength = Encoding.UTF8.GetByteCount(name);
                total += LocalHeaderLength + nameLength + size + DescriptorLength + CentralHeaderLength + nameLength;
            }

            return entries.Count == 0 || entries.Count > ushort.MaxValue || total >= uint.MaxValue ? null : total;
        }

        public void Add(string name, long size, Stream source)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var offset = (uint)_position;

            var header = new byte[LocalHeaderLength];
            PutUInt32(header, 0, 0x04034b50);
            PutUInt16(header, 4, Version);
            PutUInt16(header, 6, Flags);
            PutUInt16(header, 12, DosDate);
            PutUInt16(header, 26, (ushort)nameBytes.Length);
            Write(header);
            Write(nameBytes);

            var crc = CopyExactly(source, size);

            var descriptor = new byte[DescriptorLength];
            PutUInt32(descriptor, 0, 0x08074b50);
            PutUInt32(descriptor, 4, crc);
            PutUInt32(descriptor, 8, (uint)size);
            PutUInt32(descriptor, 12, (uint)size);
            Write(descriptor);

            _central.Add((nameBytes, crc, (uint)size, offset));
        }

        public void Finish()
        {
            var start = _position;
            foreach (var (name, crc, size, offset) in _central)
            {
                var header = new byte[CentralHeaderLength];
                PutUInt32(header, 0, 0x02014b50);
                PutUInt16(header, 4, Version);
                PutUInt16(header, 6, Version);
                PutUInt16(header, 8, Flags);
                PutUInt16(header, 14, DosDate);
                PutUInt32(header, 16, crc);
                PutUInt32(header, 20, size);
                PutUInt32(header, 24, size);
                PutUInt16(header, 28, (ushort)name.Length);
                PutUInt32(header, 42, offset);
                Write(header);
                Write(name);
            }

            var end = new byte[EndLength];
            PutUInt32(end, 0, 0x06054b50);
            PutUInt16(end, 8, (ushort)_central.Count);
            PutUInt16(end, 10, (ushort)_central.Count);
            PutUInt32(end, 12, (uint)(_position - start));
            PutUInt32(end, 16, (uint)start);
            Write(end);
            _output.Flush();
        }

        // Copies exactly the size the length was worked out from. A source that runs short would
        // leave the zip shorter than promised, so that fails rather than sending a broken file.
        private uint CopyExactly(Stream source, long size)
        {
            var crc = 0xFFFFFFFFu;
            var remaining = size;
            while (remaining > 0)
            {
                var read = source.Read(_buffer, 0, (int)Math.Min(_buffer.Length, remaining));
                if (read <= 0)
                {
                    throw new EndOfStreamException("A disc file ended before its listed size.");
                }

                for (var i = 0; i < read; i++)
                {
                    crc = CrcTable[(crc ^ _buffer[i]) & 0xFF] ^ (crc >> 8);
                }

                _output.Write(_buffer, 0, read);
                _position += read;
                remaining -= read;
            }

            return crc ^ 0xFFFFFFFFu;
        }

        private void Write(byte[] bytes)
        {
            _output.Write(bytes, 0, bytes.Length);
            _position += bytes.Length;
        }

        private static void PutUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
        }

        private static void PutUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < table.Length; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }
    }
}
