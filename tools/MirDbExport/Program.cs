using System.Globalization;

namespace MirDbExport
{
    /// <summary>
    /// Roadmap P7 step 1 CLI. Exit codes: 0 success / verified with 0 mismatches; 1 export errors or verify mismatches;
    /// 2 usage or setup error (missing file, LoadDB refused, ...).
    /// </summary>
    internal static class Program
    {
        private const int MaxListed = 200;

        private const string Usage =
@"MirDbExport - read-only SQLite export of Server.MirDB (roadmap P7 step 1)

Usage:
  MirDbExport export <folder-with-Server.MirDB> <out.db>
      Copies Server.MirDB to a temp folder, loads it into Envir.Edit with the server's own code and writes
      every Game Data registry table to <out.db> (via <out.db>.tmp, moved into place only on success).
      The source folder is never written. Any value that cannot be encoded fails the export (exit 1).

  MirDbExport verify <folder-with-Server.MirDB> <export.db>
      Reloads Server.MirDB and compares every table / row / column of <export.db> against the entities,
      using an independent re-implementation of the encoding. Exit 0 only on 0 mismatches.

  MirDbExport gaps
      Prints the completeness report: public model members no exported column carries.

  MirDbExport import <folder-with-Server.MirDB> <SystemData.db>
      Loads Server.MirDB (as above) and writes the server's SQLite store ([Database] Backend=Sqlite, roadmap P7)
      with the server's own codec. Refuses to overwrite an existing store.

  MirDbExport export-binary <SystemData.db> <out-folder> [--force]
      The rollback: writes <out-folder>\Server.MirDB from a SQLite store. Quests carry only Index, Name and Group
      (the legacy quest fields are written as defaults since quest V2 wave 4, the NPC quest lists empty); the quest V2
      definitions live in SystemData.db only.

  MirDbExport roundtrip <folder> [<store.db>]
      Loads <store.db> (default <folder>\SystemData.db), writes it as binary and compares the bytes with the
      Server.MirDB or Back Up\Database\*.bak in <folder> whose SHA-256 the store recorded at import.
      Exit 0 only when identical (no design edits since the import, and no legacy quest data in the source: wave 4
      writes those fields as defaults).

  MirDbExport check-files <server-folder>
      Imports the folder's design files (Configs\*.ini, Envir\Drops, Recipe, Routes) into a scratch
      store built from its Server.MirDB and checks that every file renders back byte for byte (roadmap P7 7.4).

  MirDbExport check-drops <server-folder>
      Copies the folder's SystemData.db, opens the copy (which converts drop_lines into the drop tables) and checks
      that every drop list loads from the tables exactly as the text loaders loaded its text, logs the same bad
      lines and renders back to the same text (Drops-Tables-Plan phase 1). Exit 0 only on 0 differences.

  MirDbExport import-files <SystemData.db> <server-folder>
      Imports the folder's design files into an existing store (the server does this on first use).

  MirDbExport export-userdata <UserData.db> <out-folder> [--force]
      The D1 rollback for user data (UserData-and-Quests-Plan.md Part A): writes <out-folder>\Server.MirADB,
      Guilds\<n>.mgd, Envir\Goods\<npc>.msd and Conquests\<index>.mcd from UserData.db, byte for byte the files an
      older build reads. Run it with the server stopped. Refuses existing targets unless --force.

  MirDbExport export-files <SystemData.db> <out-folder> [--force]
      The rollback for the design files: writes every ini and text design file the store holds under
      <out-folder> (Configs\..., Envir\...); Setup.ini's [Game]/[Items] are merged into <out-folder>\Configs\Setup.ini.

Never point it at a live server folder that is being written; copy the file first (the tool only reads it).";

        public static int Main(string[] args)
        {
            try
            {
                if (args.Length == 3 && args[0] == "export") return RunExport(args[1], args[2]);
                if (args.Length == 3 && args[0] == "verify") return RunVerify(args[1], args[2]);
                if (args.Length == 1 && args[0] == "gaps") return RunGaps();
                if (args.Length == 3 && args[0] == "import") return StoreCommands.Import(args[1], args[2]);
                if ((args.Length == 3 || (args.Length == 4 && args[3] == "--force")) && args[0] == "export-binary")
                    return StoreCommands.ExportBinary(args[1], args[2], args.Length == 4);
                if (args.Length == 2 && args[0] == "check-files") return DesignCommands.CheckFiles(args[1]);
                if (args.Length == 2 && args[0] == "check-drops") return DropCommands.CheckDrops(args[1]);
                if (args.Length == 3 && args[0] == "import-files") return DesignCommands.ImportFiles(args[1], args[2]);
                if ((args.Length == 3 || (args.Length == 4 && args[3] == "--force")) && args[0] == "export-files")
                    return DesignCommands.ExportFiles(args[1], args[2], args.Length == 4);
                if ((args.Length == 2 || args.Length == 3) && args[0] == "roundtrip") return StoreCommands.Roundtrip(args[1], args.Length == 3 ? args[2] : null);
                if ((args.Length == 3 || (args.Length == 4 && args[3] == "--force")) && args[0] == "export-userdata")
                    return StoreCommands.ExportUserData(args[1], args[2], args.Length == 4);
                Console.Error.WriteLine(Usage);
                return 2;
            }
            catch (ToolException ex)
            {
                Console.Error.WriteLine("ERROR: " + ex.Message);
                return 2;
            }
            catch (InvalidDataException ex)
            {
                Console.Error.WriteLine("ERROR: " + ex.Message);
                return 1;
            }
        }

        private static int RunExport(string folder, string outPath)
        {
            var r = Exporter.Export(folder, outPath);
            PrintTables(r.Tables);
            if (r.Errors.Count > 0)
            {
                Console.Error.WriteLine("EXPORT FAILED: " + r.Errors.Count.ToString(CultureInfo.InvariantCulture) + " error(s); no output written.");
                foreach (var e in r.Errors.Take(MaxListed)) Console.Error.WriteLine("  " + e);
                if (r.Errors.Count > MaxListed) Console.Error.WriteLine("  ... and " + (r.Errors.Count - MaxListed) + " more");
                return 1;
            }

            Console.WriteLine("Exported " + r.Tables.Count + " tables, " + r.TotalRows + " rows to " + Path.GetFullPath(outPath));
            return 0;
        }

        private static int RunVerify(string folder, string dbPath)
        {
            var r = Verifier.Verify(folder, dbPath);
            PrintTables(r.Tables);
            Console.WriteLine("Compared " + r.Tables.Count + " tables, " + r.TotalRows + " rows, " + r.CellsCompared + " cells.");
            if (r.Mismatches.Count > 0)
            {
                Console.Error.WriteLine("VERIFY FAILED: " + r.Mismatches.Count + " mismatch(es).");
                foreach (var m in r.Mismatches.Take(MaxListed)) Console.Error.WriteLine("  " + m);
                if (r.Mismatches.Count > MaxListed) Console.Error.WriteLine("  ... and " + (r.Mismatches.Count - MaxListed) + " more");
                return 1;
            }

            Console.WriteLine("VERIFIED: 0 mismatches.");
            return 0;
        }

        private static int RunGaps()
        {
            var gaps = Completeness.Compute(out var problems);
            Console.Write(Completeness.Render(gaps));
            foreach (var p in problems) Console.Error.WriteLine("PROBLEM: " + p);
            return problems.Count == 0 ? 0 : 1;
        }

        private static void PrintTables(List<(string Table, int Rows, int Columns)> tables)
        {
            foreach (var (t, rows, cols) in tables)
                Console.WriteLine("  " + t.PadRight(24) + rows.ToString(CultureInfo.InvariantCulture).PadLeft(7) + " rows " + cols.ToString(CultureInfo.InvariantCulture).PadLeft(5) + " columns");
        }
    }
}
