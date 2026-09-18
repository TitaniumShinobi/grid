using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Grid.Mo2.Models;
using Grid.Mo2.Services;

namespace Grid.Mo2.Infrastructure;

public sealed class Mo2TextDecoder : IMo2TextDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    static Mo2TextDecoder() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public Mo2RawTextDocument Decode(
        ImmutableArray<byte> bytes,
        Mo2TextDecodingPolicy policy = Mo2TextDecodingPolicy.IniBomUtf8SystemFallback)
    {
        if (bytes.IsDefault)
        {
            throw new ArgumentException("Raw bytes must be initialized.", nameof(bytes));
        }

        var span = bytes.AsSpan();
        var (kind, encoding, bomLength, unitSize, littleEndian, usedFallback) = DetectEncoding(span, policy);
        var lines = ImmutableArray.CreateBuilder<Mo2RawLine>();
        var lineStart = bomLength;
        var lineIndex = 0;
        for (var index = bomLength; index + unitSize <= span.Length; index += unitSize)
        {
            var value = ReadCodeUnit(span, index, unitSize, littleEndian);
            if (value is not ('\r' or '\n'))
            {
                continue;
            }

            var terminatorLength = unitSize;
            var terminator = value == '\r'
                ? Mo2LineTerminator.CarriageReturn
                : Mo2LineTerminator.LineFeed;
            if (value == '\r' && index + (2 * unitSize) <= span.Length &&
                ReadCodeUnit(span, index + unitSize, unitSize, littleEndian) == '\n')
            {
                terminatorLength += unitSize;
                terminator = Mo2LineTerminator.CarriageReturnLineFeed;
            }

            lines.Add(new(
                lineIndex++,
                lineStart,
                index - lineStart,
                terminatorLength,
                terminator,
                DecodeLine(span[lineStart..index], encoding)));
            index += terminatorLength - unitSize;
            lineStart = index + unitSize;
        }

        if (lineStart < span.Length)
        {
            lines.Add(new(
                lineIndex,
                lineStart,
                span.Length - lineStart,
                0,
                Mo2LineTerminator.None,
                DecodeLine(span[lineStart..], encoding)));
        }

        return new(bytes, kind, bomLength, usedFallback, lines.ToImmutable());
    }

    private static (Mo2TextEncodingKind Kind, Encoding Encoding, int Bom, int Unit, bool LittleEndian, bool UsedFallback)
        DetectEncoding(ReadOnlySpan<byte> bytes, Mo2TextDecodingPolicy policy)
    {
        if (policy == Mo2TextDecodingPolicy.WindowsSystemCodePage)
        {
            var system = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
            return (Mo2TextEncodingKind.WindowsSystemCodePage, system, 0, 1, true, false);
        }

        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            return (Mo2TextEncodingKind.Utf8, StrictUtf8, 3, 1, true, false);
        }

        if (policy == Mo2TextDecodingPolicy.IniBomUtf8SystemFallback &&
            bytes.StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            return (Mo2TextEncodingKind.Utf16LittleEndian, Encoding.Unicode, 2, 2, true, false);
        }

        if (policy == Mo2TextDecodingPolicy.IniBomUtf8SystemFallback &&
            bytes.StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            return (Mo2TextEncodingKind.Utf16BigEndian, Encoding.BigEndianUnicode, 2, 2, false, false);
        }

        try
        {
            _ = StrictUtf8.GetString(bytes);
            return (Mo2TextEncodingKind.Utf8, StrictUtf8, 0, 1, true, false);
        }
        catch (DecoderFallbackException)
        {
            if (policy == Mo2TextDecodingPolicy.StrictUtf8)
            {
                throw new InvalidDataException("The source is not valid UTF-8.");
            }

            var system = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
            return (Mo2TextEncodingKind.WindowsSystemCodePage, system, 0, 1, true, true);
        }
    }

    private static char ReadCodeUnit(
        ReadOnlySpan<byte> bytes,
        int index,
        int unitSize,
        bool littleEndian) =>
        unitSize == 1
            ? (char)bytes[index]
            : littleEndian
                ? (char)(bytes[index] | (bytes[index + 1] << 8))
                : (char)((bytes[index] << 8) | bytes[index + 1]);

    private static string DecodeLine(ReadOnlySpan<byte> bytes, Encoding encoding) => encoding.GetString(bytes);
}
