using System.Collections.Concurrent;
using System.Data.SQLite;
using Arithmic;
using Tiger.Exporters;

namespace Tiger.Commandlets;

/// <summary>
/// Scans all investment/client_startup packages and writes every file's metadata
/// (hash, reference-hash, type, subtype, size) to a SQLite database.
///
/// This is a schema-discovery tool. Its primary purpose is to catalogue the reference
/// hashes present in the investment packages so that unknown definition-table hashes
/// (e.g. the contract-definition table) can be spotted next to already-mapped ones.
///
/// Two tables are produced:
///   AllFiles              – every file found in matching packages.
///   DefinitionMapCandidates – files where Type == 8 and SubType == 0,
///                             the same pattern used by all existing definition maps
///                             (inventory items, objectives, records, stats, …).
///
/// Usage:
///   -commandlet=DumpInvestmentFileTypes
///   [-packageFilter=investment]   (pipe-separated substrings; default "investment")
/// </summary>
public class DumpInvestmentFileTypesCommandlet : ICommandlet
{
    public void Run(InstanceArgs args)
    {
        args.GetArgValue("packageFilter", out string packageFilter);
        if (string.IsNullOrWhiteSpace(packageFilter))
            packageFilter = "investment";

        string[] filterTerms = packageFilter.Split('|');
        bool PackageFilterFunc(string path) => filterTerms.Any(t => path.Contains(t, StringComparison.OrdinalIgnoreCase));

        Dictionary<ushort, string> allPackagesMap = PackageResourcer.Get().PackagePathsCache.GetAllPackagesMap();
        List<ushort> packageIds = allPackagesMap
            .Where(pair => PackageFilterFunc(pair.Value))
            .Select(pair => pair.Key)
            .ToList();

        Log.Info($"Scanning {packageIds.Count} packages matching filter '{packageFilter}'");

        ConcurrentBag<FileTypeEntry> allEntries = new();
        ConcurrentBag<FileTypeEntry> candidateEntries = new();

        Parallel.ForEach(packageIds, packageId =>
        {
            Package package = PackageResourcer.Get().GetPackage(packageId);
            PackageMetadata pkgMeta = package.GetPackageMetadata();

            foreach (FileMetadata fileMeta in package.GetAllFileMetadata())
            {
                FileTypeEntry entry = new FileTypeEntry
                {
                    PackageName = pkgMeta.Name,
                    Hash = fileMeta.Hash,
                    ReferenceHash = fileMeta.Reference,
                    Type = fileMeta.Type,
                    SubType = fileMeta.SubType,
                    Size = fileMeta.Size
                };

                allEntries.Add(entry);

                if (fileMeta.Type == 8 && fileMeta.SubType == 0)
                    candidateEntries.Add(entry);
            }
        });

        Log.Info($"Total files found: {allEntries.Count}");
        Log.Info($"Definition-map candidates (Type=8, SubType=0): {candidateEntries.Count}");

        string databasePath = $"./InvestmentFileDatabases/{Strategy.CurrentStrategy}.db";
        string connectionString = $"Data Source=\"{databasePath}\";Version=3;";

        Directory.CreateDirectory(Path.GetDirectoryName(databasePath));
        SQLiteConnection.CreateFile(databasePath);

        using SQLiteConnection connection = new(connectionString);
        connection.Open();
        SQLiteTransaction transaction = connection.BeginTransaction();
        SQLHandle handle = new(connection, transaction);

        SQLTable<FileTypeEntry> allTable = new("AllFiles");
        allTable.CreateTable(connection);
        allTable.InsertValues(handle, allEntries);

        SQLTable<FileTypeEntry> candidateTable = new("DefinitionMapCandidates");
        candidateTable.CreateTable(connection);
        candidateTable.InsertValues(handle, candidateEntries);

        transaction.Commit();

        Log.Info($"Database saved to {databasePath}");
        Log.Info("Query suggestion to find unmapped reference hashes:");
        Log.Info("  SELECT ReferenceHash, COUNT(*) AS Count, MIN(Size) AS MinSize, MAX(Size) AS MaxSize");
        Log.Info("  FROM DefinitionMapCandidates GROUP BY ReferenceHash ORDER BY Count DESC;");
    }
}

/// <summary>
/// One row in the output database tables – mirrors <see cref="FileMetadata"/> with a
/// package-name prefix so the SQLite schema maps cleanly via <see cref="SQLTable{T}"/>.
/// </summary>
public struct FileTypeEntry
{
    public string PackageName;
    public FileHash Hash;
    public TigerHash ReferenceHash;
    public sbyte Type;
    public sbyte SubType;
    public int Size;
}
