using System.Text;
using Arithmic;
using Tiger.Schema.Investment;

namespace Tiger.Commandlets;

/// <summary>
/// Loads the raw binary data for a single file by its hash, writes a binary dump to disk,
/// and produces an annotated text listing that cross-references every 4-byte word against
/// the known inventory-item index.  The annotation output is the primary artifact: it lets
/// you read a definition-table file you have not mapped yet and quickly identify which words
/// are item hashes, what their index positions are, and — by observing how often annotated
/// offsets repeat — what the entry stride (i.e. the serialised size of one entry) is.
///
/// Usage:
///   -commandlet=DumpSchemaFields -fileHash=80801234
///
/// Outputs (in ./SchemaFieldDumps/):
///   {hash}.bin              – raw binary, open in any hex editor.
///   {hash}_annotated.txt    – human-readable analysis.
/// </summary>
public class DumpSchemaFieldsCommandlet : ICommandlet
{
    public void Run(InstanceArgs args)
    {
        if (!args.GetArgValue("fileHash", out string fileHashStr))
        {
            Log.Error("No fileHash argument provided (-fileHash=XXXXXXXX)");
            return;
        }

        FileHash fileHash = new FileHash(fileHashStr);
        if (fileHash.IsInvalid())
        {
            Log.Error($"Invalid or unresolvable file hash '{fileHashStr}'");
            return;
        }

        byte[] data = PackageResourcer.Get().GetFileData(fileHash);
        if (data == null || data.Length == 0)
        {
            Log.Error($"No data returned for hash {fileHash}");
            return;
        }

        string outputDir = "./SchemaFieldDumps";
        Directory.CreateDirectory(outputDir);

        // ----- 1. Raw binary dump -----
        string binPath = Path.Combine(outputDir, $"{fileHash}.bin");
        File.WriteAllBytes(binPath, data);
        Log.Info($"Binary dump written: {binPath}");

        // ----- 2. Load item index for cross-referencing (optional) -----
        Investment? investment = null;
        try
        {
            Investment.LazyInit();
            investment = Investment.Get();
            Log.Info("Investment data loaded – item hashes will be annotated.");
        }
        catch (Exception ex)
        {
            Log.Warning($"Could not load Investment data; known-hash annotations will be omitted. ({ex.Message})");
        }

        // ----- 3. Build the annotated text -----
        StringBuilder sb = new();
        sb.AppendLine($"=== Schema Field Dump: {fileHash} ===");
        sb.AppendLine($"File size : {data.Length} bytes (0x{data.Length:X})");
        sb.AppendLine($"Binary    : {binPath}");
        sb.AppendLine();

        AppendFileHeader(sb, data);
        List<int> annotatedOffsets = AppendKnownHashTable(sb, data, investment);
        AppendStrideAnalysis(sb, annotatedOffsets, data.Length);

        string txtPath = Path.Combine(outputDir, $"{fileHash}_annotated.txt");
        File.WriteAllText(txtPath, sb.ToString());
        Log.Info($"Annotated dump written: {txtPath}");
    }

    // -------------------------------------------------------------------------

    /// <summary>Interprets the first 0x30 bytes as the standard definition-map file header.</summary>
    private static void AppendFileHeader(StringBuilder sb, byte[] data)
    {
        sb.AppendLine("--- File Header (first 0x30 bytes) ---");
        sb.AppendLine("Offset   RawBytes          Interpreted");

        for (int offset = 0; offset < Math.Min(0x30, data.Length - 7); offset += 8)
        {
            ulong value = BitConverter.ToUInt64(data, offset);
            string raw = BitConverter.ToString(data, offset, Math.Min(8, data.Length - offset)).Replace("-", " ");
            string label = offset switch
            {
                0x00 => $"FileSize = {value} (0x{value:X})",
                0x08 => InterpretAsArrayHeader(data, offset),
                0x10 => InterpretAsArrayHeader(data, offset),
                0x18 => InterpretAsArrayHeader(data, offset),
                _ => $"0x{value:X16}"
            };
            sb.AppendLine($"0x{offset:X4}:   {raw,-24}  {label}");
        }

        sb.AppendLine();
    }

    /// <summary>
    /// Reads a Tiger DynamicArray header (uint32 Count + uint32 Offset, little-endian)
    /// at a given position in the file and formats it as a human-readable string.
    /// </summary>
    private static string InterpretAsArrayHeader(byte[] data, int offset)
    {
        if (offset + 8 > data.Length)
            return "(truncated)";

        uint count = BitConverter.ToUInt32(data, offset);
        uint arrayOffset = BitConverter.ToUInt32(data, offset + 4);
        return $"DynamicArray {{ Count = {count} (0x{count:X}), Offset = 0x{arrayOffset:X} }}";
    }

    /// <summary>
    /// Walks every 4-byte word in the file and, when it matches a known inventory-item hash,
    /// records the offset with an annotation.  Returns the list of annotated offsets (used
    /// later for stride analysis).
    /// </summary>
    private static List<int> AppendKnownHashTable(StringBuilder sb, byte[] data, Investment? investment)
    {
        sb.AppendLine("--- Known-Hash Annotations ---");

        List<int> annotatedOffsets = new();

        if (investment == null)
        {
            sb.AppendLine("(Investment not loaded – skipped)");
            sb.AppendLine();
            return annotatedOffsets;
        }

        sb.AppendLine($"{"Offset",-10} {"Value (LE)",-12} {"Annotation"}");
        sb.AppendLine(new string('-', 60));

        int wordCount = data.Length / 4;
        bool anyFound = false;

        for (int i = 0; i < wordCount; i++)
        {
            int offset = i * 4;
            uint word = BitConverter.ToUInt32(data, offset);

            if (investment.TryGetInventoryItemIndex(word, out int itemIndex))
            {
                sb.AppendLine($"0x{offset:X4}     0x{word:X8}   [InventoryItem index={itemIndex}]");
                annotatedOffsets.Add(offset);
                anyFound = true;
            }
        }

        if (!anyFound)
            sb.AppendLine("(No known inventory-item hashes found in this file)");

        sb.AppendLine();
        return annotatedOffsets;
    }

    /// <summary>
    /// Analyses the gaps between consecutive annotated offsets to infer the likely entry
    /// stride (serialised size of one definition-table entry).
    /// </summary>
    private static void AppendStrideAnalysis(StringBuilder sb, List<int> annotatedOffsets, int fileLength)
    {
        sb.AppendLine("--- Stride Analysis ---");

        if (annotatedOffsets.Count < 2)
        {
            sb.AppendLine("(Need at least 2 annotated offsets for stride analysis)");
            return;
        }

        // Compute all consecutive gaps
        List<int> gaps = new(annotatedOffsets.Count - 1);
        for (int i = 1; i < annotatedOffsets.Count; i++)
            gaps.Add(annotatedOffsets[i] - annotatedOffsets[i - 1]);

        // Find the most common gap value
        var gapFrequencies = gaps
            .GroupBy(g => g)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToList();

        int dominantGap = gapFrequencies[0].Key;
        int dominantCount = gapFrequencies[0].Count();
        double consistency = (double)dominantCount / gaps.Count * 100.0;

        sb.AppendLine($"Annotated offsets  : {annotatedOffsets.Count}");
        sb.AppendLine($"Dominant stride    : 0x{dominantGap:X} ({dominantGap} bytes) — seen {dominantCount}/{gaps.Count} times ({consistency:F0}% consistent)");
        sb.AppendLine();

        if (consistency >= 80.0)
        {
            sb.AppendLine($"  >> High confidence: entry size is likely 0x{dominantGap:X} bytes.");
            sb.AppendLine($"     Suggested [SchemaStruct] attribute:");
            sb.AppendLine($"       [SchemaStruct(TigerStrategy.MARATHON_ALPHA, \"XXXXXXXX\", 0x{dominantGap:X})]");
        }
        else
        {
            sb.AppendLine("  >> Low confidence – multiple different strides detected:");
            foreach (var group in gapFrequencies.Take(5))
                sb.AppendLine($"     0x{group.Key:X} ({group.Key} bytes) x{group.Count()}");
        }

        sb.AppendLine();
        sb.AppendLine("All annotated offsets (for manual inspection):");
        foreach (int offset in annotatedOffsets)
            sb.AppendLine($"  0x{offset:X4}");
    }
}
