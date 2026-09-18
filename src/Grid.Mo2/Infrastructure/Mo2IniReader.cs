using System.Globalization;
using System.Text;
using Grid.Mo2.Services;

namespace Grid.Mo2.Infrastructure;

public sealed class Mo2IniReader(IMo2ReadOnlyFileSystem fileSystem) : IMo2IniReader
{
    private const int MaximumIniBytes = 1024 * 1024;
    private const int MaximumLineCharacters = 16 * 1024;

    public async Task<Mo2IniDocument> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var content = await fileSystem.ReadTextAsync(path, MaximumIniBytes, cancellationToken).ConfigureAwait(false);
        var sections = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var mutableSections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var currentSection = string.Empty;
        mutableSections[currentSection] = new(StringComparer.OrdinalIgnoreCase);

        using var reader = new StringReader(content);
        var lineNumber = 0;
        while (reader.ReadLine() is { } rawLine)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            if (rawLine.Length > MaximumLineCharacters)
            {
                throw new InvalidDataException($"INI line {lineNumber} exceeds the supported length.");
            }
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            if (line[0] == '[')
            {
                if (line[^1] != ']' || line.Length < 3)
                {
                    throw new InvalidDataException($"Malformed INI section at line {lineNumber}.");
                }

                currentSection = line[1..^1].Trim();
                if (currentSection.Length == 0)
                {
                    throw new InvalidDataException($"Empty INI section at line {lineNumber}.");
                }

                mutableSections.TryAdd(currentSection, new(StringComparer.OrdinalIgnoreCase));
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                throw new InvalidDataException($"Malformed INI entry at line {lineNumber}.");
            }

            var key = line[..separator].Trim();
            if (key.Length == 0)
            {
                throw new InvalidDataException($"Empty INI key at line {lineNumber}.");
            }

            mutableSections[currentSection][key] = DecodeQtValue(line[(separator + 1)..].Trim());
        }

        foreach (var section in mutableSections)
        {
            sections.Add(section.Key, section.Value);
        }

        return new Mo2IniDocument(sections);
    }

    internal static string DecodeQtValue(string value)
    {
        if (value.StartsWith("@ByteArray(", StringComparison.Ordinal) && value.EndsWith(')'))
        {
            value = value[11..^1];
        }

        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\' || index + 1 >= value.Length)
            {
                builder.Append(value[index]);
                continue;
            }

            var escaped = value[++index];
            switch (escaped)
            {
                case '\\': builder.Append('\\'); break;
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;
                case 'x':
                    var start = index + 1;
                    var count = 0;
                    while (start + count < value.Length && count < 4 && Uri.IsHexDigit(value[start + count]))
                    {
                        count++;
                    }

                    if (count == 0)
                    {
                        builder.Append('x');
                    }
                    else
                    {
                        builder.Append((char)int.Parse(value.AsSpan(start, count), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        index += count;
                    }
                    break;
                default: builder.Append(escaped); break;
            }
        }

        return builder.ToString();
    }
}
