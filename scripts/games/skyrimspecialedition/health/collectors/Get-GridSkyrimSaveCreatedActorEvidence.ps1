#requires -Version 5.1

<#
.SYNOPSIS
Reads created actor references from Skyrim Special Edition saves without modifying them.
.DESCRIPTION
Parses the bounded ESS structures needed to resolve created ACHR references to their
base records. The collector accepts a plugin and local FormIDs so evidence remains
stable across load-order indices. It never writes to the source save.
#>

if (-not ('Grid.SkyrimSave.CreatedActorReader' -as [type])) {
    # Grid is launched beside its self-contained .NET runtime. Bind CodeDOM to
    # the Windows PowerShell host framework explicitly so same-named packaged
    # facade assemblies cannot redirect types to System.Private.CoreLib.
    $frameworkReferences = @(
        [object].Assembly.Location
        [System.Uri].Assembly.Location
        [System.Linq.Enumerable].Assembly.Location
    ) | Select-Object -Unique
    $readerSource = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Grid.SkyrimSave
{
    public sealed class CreatedActorEvidence
    {
        public string ReferenceFormId { get; set; }
        public string BaseFormId { get; set; }
        public string CellFormId { get; set; }
        public uint ChangeFlags { get; set; }
        public byte ChangeVersion { get; set; }
        public float PositionX { get; set; }
        public float PositionY { get; set; }
        public float PositionZ { get; set; }
    }

    public sealed class SaveEvidence
    {
        public string Path { get; set; }
        public string PlayerName { get; set; }
        public string Location { get; set; }
        public string GameDate { get; set; }
        public int SaveNumber { get; set; }
        public int Version { get; set; }
        public byte FormVersion { get; set; }
        public string Compression { get; set; }
        public string PluginName { get; set; }
        public int PluginIndex { get; set; }
        public int ChangeFormCount { get; set; }
        public int CreatedActorCount { get; set; }
        public List<CreatedActorEvidence> Matches { get; set; }
    }

    public static class CreatedActorReader
    {
        public static SaveEvidence Inspect(string path, string pluginName, uint[] localFormIds)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("Save path is required.");
            if (String.IsNullOrWhiteSpace(pluginName)) throw new ArgumentException("Plugin name is required.");
            if (localFormIds == null || localFormIds.Length == 0) throw new ArgumentException("At least one local FormID is required.");

            byte[] file = File.ReadAllBytes(Path.GetFullPath(path));
            var input = new Reader(file);
            string magic = Encoding.ASCII.GetString(input.ReadBytes(13));
            if (!String.Equals(magic, "TESV_SAVEGAME", StringComparison.Ordinal))
                throw new InvalidDataException("The file is not a TESV savegame.");

            int headerSize = input.ReadInt32();
            int version = input.ReadInt32();
            if (version < 12) throw new InvalidDataException("Only Skyrim Special Edition saves are supported.");
            int saveNumber = input.ReadInt32();
            string playerName = input.ReadWString();
            input.ReadInt32(); // player level
            string location = input.ReadWString();
            string gameDate = input.ReadWString();
            input.ReadWString(); // race editor ID
            input.ReadUInt16(); // sex
            input.ReadSingle();
            input.ReadSingle();
            input.ReadInt64();
            int screenshotWidth = input.ReadInt32();
            int screenshotHeight = input.ReadInt32();
            ushort compression = input.ReadUInt16();

            int expectedHeaderSize = input.Position - 17;
            if (headerSize != expectedHeaderSize)
                throw new InvalidDataException("The ESS header size is inconsistent.");
            long screenshotBytes = checked((long)screenshotWidth * screenshotHeight * 4L);
            input.Skip(checked((int)screenshotBytes));
            int startingOffset = input.Position;

            byte[] body;
            string compressionName;
            if (compression == 0)
            {
                body = input.ReadBytes(input.Remaining);
                compressionName = "Uncompressed";
            }
            else
            {
                int uncompressedLength = input.ReadInt32();
                int compressedLength = input.ReadInt32();
                if (uncompressedLength < 0 || compressedLength < 0 || compressedLength != input.Remaining)
                    throw new InvalidDataException("The ESS compressed body lengths are inconsistent.");
                byte[] compressedBody = input.ReadBytes(compressedLength);
                if (compression == 1)
                {
                    body = InflateZlib(compressedBody, uncompressedLength);
                    compressionName = "Zlib";
                }
                else if (compression == 2)
                {
                    body = DecodeLz4Block(compressedBody, uncompressedLength);
                    compressionName = "LZ4";
                }
                else
                {
                    throw new InvalidDataException("Unsupported ESS compression type: " + compression);
                }
            }

            var data = new Reader(body);
            byte formVersion = data.ReadByte();
            int pluginInfoStart = data.Position;
            int pluginInfoSize = data.ReadInt32();
            int fullPluginCount = data.ReadByte();
            var fullPlugins = new List<string>(fullPluginCount);
            for (int i = 0; i < fullPluginCount; i++) fullPlugins.Add(data.ReadWString());
            int litePluginCount = data.ReadUInt16();
            for (int i = 0; i < litePluginCount; i++) data.ReadWString();
            if (data.Position != pluginInfoStart + 4 + pluginInfoSize)
                throw new InvalidDataException("The ESS plugin table size is inconsistent.");

            int pluginIndex = -1;
            for (int i = 0; i < fullPlugins.Count; i++)
            {
                if (String.Equals(fullPlugins[i], pluginName, StringComparison.OrdinalIgnoreCase))
                {
                    pluginIndex = i;
                    break;
                }
            }
            if (pluginIndex < 0) throw new InvalidDataException("Plugin is absent from the save: " + pluginName);

            int formIdArrayOffset = data.ReadInt32();
            data.ReadInt32(); // unknown table 3 offset
            data.ReadInt32(); // table 1 offset
            data.ReadInt32(); // table 2 offset
            int changeFormsOffset = data.ReadInt32();
            data.ReadInt32(); // table 3 offset
            data.ReadInt32(); // table 1 count
            data.ReadInt32(); // table 2 count
            data.ReadInt32(); // table 3 count
            int changeFormCount = data.ReadInt32();
            data.Skip(15 * 4);

            data.Position = checked(formIdArrayOffset - startingOffset);
            int formIdCount = data.ReadInt32();
            if (formIdCount < 0 || formIdCount > 10000000) throw new InvalidDataException("Invalid ESS FormID count.");
            var formIds = new uint[formIdCount];
            for (int i = 0; i < formIdCount; i++) formIds[i] = data.ReadUInt32();

            var targetIds = new HashSet<uint>();
            foreach (uint localId in localFormIds)
                targetIds.Add(((uint)pluginIndex << 24) | (localId & 0x00FFFFFFu));

            data.Position = checked(changeFormsOffset - startingOffset);
            var matches = new List<CreatedActorEvidence>();
            int createdActorCount = 0;
            for (int i = 0; i < changeFormCount; i++)
            {
                uint rawReference = data.ReadRefIdRaw();
                uint changeFlags = data.ReadUInt32();
                byte typeField = data.ReadByte();
                byte changeVersion = data.ReadByte();
                int lengthSize = typeField >> 6;
                int length1;
                int length2;
                if (lengthSize == 0)
                {
                    length1 = data.ReadByte();
                    length2 = data.ReadByte();
                }
                else if (lengthSize == 1)
                {
                    length1 = data.ReadUInt16();
                    length2 = data.ReadUInt16();
                }
                else if (lengthSize == 2)
                {
                    length1 = data.ReadInt32();
                    length2 = data.ReadInt32();
                }
                else
                {
                    throw new InvalidDataException("Invalid ESS ChangeForm length encoding.");
                }
                if (length1 < 0 || length2 < 0) throw new InvalidDataException("Negative ESS ChangeForm length.");
                byte[] recordBody = data.ReadBytes(length1);

                bool isCreated = (rawReference >> 22) == 2;
                bool isActor = (typeField & 0x3F) == 1;
                if (!isCreated || !isActor) continue;
                createdActorCount++;

                if (length2 > 0) recordBody = InflateZlib(recordBody, length2);
                if (recordBody.Length < 31) continue;
                var record = new Reader(recordBody);
                uint rawCell = record.ReadRefIdRaw();
                float x = record.ReadSingle();
                float y = record.ReadSingle();
                float z = record.ReadSingle();
                record.Skip(12); // rotation
                record.ReadByte();
                uint rawBase = record.ReadRefIdRaw();
                uint baseFormId = ResolveRefId(rawBase, formIds);
                if (!targetIds.Contains(baseFormId)) continue;

                matches.Add(new CreatedActorEvidence
                {
                    ReferenceFormId = ResolveRefId(rawReference, formIds).ToString("X8"),
                    BaseFormId = baseFormId.ToString("X8"),
                    CellFormId = ResolveRefId(rawCell, formIds).ToString("X8"),
                    ChangeFlags = changeFlags,
                    ChangeVersion = changeVersion,
                    PositionX = x,
                    PositionY = y,
                    PositionZ = z
                });
            }

            matches.Sort((a, b) => StringComparer.Ordinal.Compare(a.ReferenceFormId, b.ReferenceFormId));
            return new SaveEvidence
            {
                Path = Path.GetFullPath(path),
                PlayerName = playerName,
                Location = location,
                GameDate = gameDate,
                SaveNumber = saveNumber,
                Version = version,
                FormVersion = formVersion,
                Compression = compressionName,
                PluginName = fullPlugins[pluginIndex],
                PluginIndex = pluginIndex,
                ChangeFormCount = changeFormCount,
                CreatedActorCount = createdActorCount,
                Matches = matches
            };
        }

        private static uint ResolveRefId(uint raw, uint[] formIds)
        {
            uint kind = raw >> 22;
            uint value = raw & 0x003FFFFFu;
            if (value == 0) return 0;
            if (kind == 0)
            {
                int index = checked((int)value - 1);
                if (index < 0 || index >= formIds.Length) throw new InvalidDataException("ESS RefID exceeds the FormID table.");
                return formIds[index];
            }
            if (kind == 1) return value;
            if (kind == 2) return 0xFF000000u | value;
            throw new InvalidDataException("Invalid ESS RefID kind.");
        }

        private static byte[] InflateZlib(byte[] source, int expectedLength)
        {
            if (source.Length < 6) throw new InvalidDataException("Truncated zlib stream.");
            using (var input = new MemoryStream(source, 2, source.Length - 6, false))
            using (var inflater = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream(expectedLength))
            {
                inflater.CopyTo(output);
                byte[] result = output.ToArray();
                if (result.Length != expectedLength) throw new InvalidDataException("The zlib output length is inconsistent.");
                return result;
            }
        }

        private static byte[] DecodeLz4Block(byte[] source, int expectedLength)
        {
            var output = new byte[expectedLength];
            int input = 0;
            int written = 0;
            while (written < expectedLength)
            {
                if (input >= source.Length) throw new InvalidDataException("The LZ4 token is truncated.");
                int token = source[input++];
                int literalLength = token >> 4;
                if (literalLength == 15) literalLength += ReadExtendedLength(source, ref input);
                if (literalLength > source.Length - input || literalLength > output.Length - written)
                    throw new InvalidDataException("The LZ4 literal run exceeds its bounds.");
                Buffer.BlockCopy(source, input, output, written, literalLength);
                input += literalLength;
                written += literalLength;
                if (written == expectedLength) break;
                if (source.Length - input < 2) throw new InvalidDataException("The LZ4 match offset is truncated.");
                int offset = source[input] | (source[input + 1] << 8);
                input += 2;
                if (offset <= 0 || offset > written) throw new InvalidDataException("The LZ4 match offset is invalid.");
                int matchLength = token & 0x0F;
                if (matchLength == 15) matchLength += ReadExtendedLength(source, ref input);
                matchLength += 4;
                if (matchLength > output.Length - written) throw new InvalidDataException("The LZ4 match exceeds the expanded size.");
                int match = written - offset;
                for (int j = 0; j < matchLength; j++) output[written++] = output[match + j];
            }
            if (written != expectedLength) throw new InvalidDataException("The LZ4 output length is inconsistent.");
            return output;
        }

        private static int ReadExtendedLength(byte[] source, ref int input)
        {
            int total = 0;
            int value;
            do
            {
                if (input >= source.Length) throw new InvalidDataException("The LZ4 extended length is truncated.");
                value = source[input++];
                total = checked(total + value);
            } while (value == 255);
            return total;
        }

        private sealed class Reader
        {
            private readonly byte[] data;
            public int Position { get; set; }
            public int Remaining { get { return data.Length - Position; } }
            public Reader(byte[] bytes)
            {
                if (bytes == null) throw new ArgumentNullException("bytes");
                data = bytes;
            }
            public byte ReadByte() { Require(1); return data[Position++]; }
            public ushort ReadUInt16() { Require(2); ushort value = (ushort)(data[Position] | (data[Position + 1] << 8)); Position += 2; return value; }
            public int ReadInt32() { return unchecked((int)ReadUInt32()); }
            public uint ReadUInt32() { Require(4); uint value = (uint)(data[Position] | (data[Position + 1] << 8) | (data[Position + 2] << 16) | (data[Position + 3] << 24)); Position += 4; return value; }
            public long ReadInt64() { uint low = ReadUInt32(); uint high = ReadUInt32(); return unchecked((long)(((ulong)high << 32) | low)); }
            public float ReadSingle() { byte[] bytes = ReadBytes(4); return BitConverter.ToSingle(bytes, 0); }
            public uint ReadRefIdRaw() { Require(3); uint value = (uint)((data[Position] << 16) | (data[Position + 1] << 8) | data[Position + 2]); Position += 3; return value; }
            public string ReadWString() { int length = ReadUInt16(); return Encoding.UTF8.GetString(ReadBytes(length)); }
            public byte[] ReadBytes(int count) { Require(count); var bytes = new byte[count]; Buffer.BlockCopy(data, Position, bytes, 0, count); Position += count; return bytes; }
            public void Skip(int count) { Require(count); Position += count; }
            private void Require(int count) { if (count < 0 || count > Remaining) throw new EndOfStreamException("Unexpected end of ESS data."); }
        }
    }
}
'@
    Add-Type -TypeDefinition $readerSource -ReferencedAssemblies $frameworkReferences -ErrorAction Stop
}

function Get-GridSkyrimSaveCreatedActorEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string[]]$SavePath,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$PluginName,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][uint32[]]$LocalBaseFormId,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ContextFingerprint,
        [string]$CaseDirectory
    )

    $collectedAt = [DateTimeOffset]::Now.ToString('o')
    $items = New-Object Collections.Generic.List[object]
    foreach ($candidate in $SavePath) {
        $fullPath = [IO.Path]::GetFullPath($candidate)
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "SkyrimSaveMissing: $fullPath"
        }
        $file = Get-Item -LiteralPath $fullPath
        $inspection = [Grid.SkyrimSave.CreatedActorReader]::Inspect($fullPath, $PluginName, $LocalBaseFormId)
        $items.Add([pscustomobject][ordered]@{
            id = 'skyrim-save-created-actors:' + (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
            claim = "Created ACHR references derived from '$PluginName' were parsed from the read-only save."
            sourceType = 'SkyrimSpecialEditionSave'
            sourceIdentifier = $fullPath
            collectedAt = $collectedAt
            contextFingerprint = $ContextFingerprint
            verificationStatus = 'Verified'
            collector = 'Get-GridSkyrimSaveCreatedActorEvidence/1'
            source = [pscustomobject][ordered]@{
                length = [long]$file.Length
                lastWriteTimeUtc = $file.LastWriteTimeUtc.ToString('o')
                sha256 = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
            }
            native = $inspection
        })
    }

    $document = [pscustomobject][ordered]@{
        schemaVersion = 1
        status = 'Observed'
        contextFingerprint = $ContextFingerprint
        pluginName = $PluginName
        localBaseFormIds = @($LocalBaseFormId | ForEach-Object { $_.ToString('X6') })
        mutationPerformed = $false
        evidence = $items.ToArray()
    }

    $outputPath = $null
    if (-not [string]::IsNullOrWhiteSpace($CaseDirectory)) {
        $fullCase = [IO.Path]::GetFullPath($CaseDirectory)
        if (-not (Test-Path -LiteralPath $fullCase -PathType Container)) {
            New-Item -ItemType Directory -Path $fullCase -Force | Out-Null
        }
        $outputPath = Join-Path $fullCase 'skyrim-save-created-actor-evidence.v1.json'
        $document | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $outputPath -Encoding UTF8
    }

    [pscustomobject][ordered]@{ Path = $outputPath; Evidence = $document }
}
