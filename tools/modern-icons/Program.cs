using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ModernIcons
{
    /// <summary>
    /// Generates Client/Resources/ModernIcons.Lib + Client/MirScenes/Modern/Core/ModernIcons.cs from the web UI's icons
    /// (Desktop-Modern-UI-Plan D4). Usage: see README.md.
    /// Library layout: stroke icon i (ICONS order) → image 2i (24 x 24, stroke 2) and 2i + 1 (48 x 48, stroke 4);
    /// then one image per emblem PNG (file-name order, native size).
    /// </summary>
    internal static class Program
    {
        private const int IconSize = 24;

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length > 0 && args[0] == "--verify")
                    return Verify(args);
                return Generate(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERROR: " + ex.Message);
                return 1;
            }
        }

        private static string Opt(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }

        private static string FindRepo(string given)
        {
            if (given != null) return Path.GetFullPath(given);
            foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "Legend of Mir.sln"))) return dir.FullName;
                    dir = dir.Parent;
                }
            }
            throw new InvalidOperationException("repo root (Legend of Mir.sln) not found; pass --repo <path>");
        }

        private static void RefuseBuild(string repo, string path)
        {
            string build = Path.GetFullPath(Path.Combine(repo, "Build")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(path).StartsWith(build, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"refusing to write under {build} (live run folder): {path}");
        }

        // ---------------------------------------------------------------- generate

        private static int Generate(string[] args)
        {
            string repo = FindRepo(Opt(args, "--repo"));
            string iconsTs = Path.Combine(repo, @"WebClient\src\ui\modern\core\icons.ts");
            string pngDir = Path.Combine(repo, @"WebClient\play\menu-icons");
            string libOut = Path.GetFullPath(Opt(args, "--lib") ?? Path.Combine(repo, @"Client\Resources\ModernIcons.Lib"));
            string csOut = Path.GetFullPath(Opt(args, "--cs") ?? Path.Combine(repo, @"Client\MirScenes\Modern\Core\ModernIcons.cs"));
            RefuseBuild(repo, libOut);
            RefuseBuild(repo, csOut);

            string ts = File.ReadAllText(iconsTs);
            var icons = ParseIcons(ts);
            var pictures = ParsePictures(ts);
            var artKeys = ParseArtKeys(ts);

            var images = new List<LibImage>();
            var names = new List<string>();
            var iconConsts = new List<(string name, int index)>();
            var failed = new List<string>();

            foreach (var (name, d) in icons)
            {
                SvgPath path;
                try { path = SvgPath.Parse(d); }
                catch (Exception ex) { failed.Add($"{name}: {ex.Message}"); continue; }
                iconConsts.Add((name, images.Count));
                foreach (int scale in new[] { 1, 2 })
                {
                    images.Add(RenderIcon(path, scale));
                    names.Add(scale == 1 ? name : name + "@2x");
                }
            }

            int firstEmblem = images.Count;
            var emblemConsts = new List<(string stem, int index)>();
            var pngs = Directory.GetFiles(pngDir, "*.png");
            Array.Sort(pngs, StringComparer.OrdinalIgnoreCase);
            foreach (string png in pngs)
            {
                string stem = Path.GetFileNameWithoutExtension(png);
                using var src = new Bitmap(png);
                using var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height));
                }
                emblemConsts.Add((stem, images.Count));
                images.Add(LibImage.FromBitmap(bmp));
                names.Add("emblem:" + stem);
            }

            var emblemKeys = new List<(string key, string stem)>();
            foreach (var (key, stem) in pictures)
            {
                if (emblemConsts.Exists(e => e.stem == stem)) emblemKeys.Add((key, stem));
                else failed.Add($"PICTURES[\"{key}\"] = \"{stem}\": no {stem}.png");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(libOut));
            LibWriter.Write(libOut, images);
            Directory.CreateDirectory(Path.GetDirectoryName(csOut));
            string cs = EmitCs(iconConsts, emblemConsts, emblemKeys, names, artKeys, firstEmblem, images.Count);
            File.WriteAllText(csOut, cs, new UTF8Encoding(true));

            Console.WriteLine($"stroke icons: {iconConsts.Count} ({iconConsts.Count * 2} images, 1x + 2x)");
            Console.WriteLine($"emblems: {emblemConsts.Count} (images {firstEmblem}..{images.Count - 1}), {emblemKeys.Count} PICTURES keys");
            Console.WriteLine($"not included (ART svg emblems): {string.Join(", ", artKeys)}");
            Console.WriteLine($"wrote {libOut} ({new FileInfo(libOut).Length} bytes, {images.Count} images)");
            Console.WriteLine($"wrote {csOut}");
            foreach (string f in failed) Console.WriteLine("FAILED " + f);
            return failed.Count == 0 ? 0 : 2;
        }

        /// <summary>White strokes on transparent, like CSS svg.ic: stroke 2 (× scale), round caps and joins, no fill.</summary>
        private static LibImage RenderIcon(SvgPath path, int scale)
        {
            int size = IconSize * scale;
            using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality; // SVG coordinates: integers are pixel edges
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.ScaleTransform(scale, scale);
                using var pen = new Pen(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawPath(pen, path.Path);
                using var brush = new SolidBrush(Color.White);
                foreach (var p in path.Dots)
                    g.FillEllipse(brush, p.X - 1f, p.Y - 1f, 2f, 2f);
            }
            var img = LibImage.FromBitmap(bmp);
            // Pure white with coverage in alpha (tinted at draw time); fully transparent pixels are 0,0,0,0.
            var px = img.Pixels;
            for (int i = 0; i < px.Length; i += 4)
            {
                byte v = px[i + 3] == 0 ? (byte)0 : (byte)255;
                px[i] = px[i + 1] = px[i + 2] = v;
            }
            return img;
        }

        // ---------------------------------------------------------------- icons.ts parsing

        private static string Block(string ts, string startPattern, string endPattern)
        {
            var m = Regex.Match(ts, startPattern);
            if (!m.Success) throw new InvalidDataException($"icons.ts: '{startPattern}' not found");
            int start = m.Index + m.Length;
            var e = new Regex(endPattern, RegexOptions.Multiline).Match(ts, start);
            if (!e.Success) throw new InvalidDataException($"icons.ts: end of block after '{startPattern}' not found");
            return ts.Substring(start, e.Index - start);
        }

        private static List<(string, string)> ParseIcons(string ts)
        {
            string body = Block(ts, @"export const ICONS\s*=\s*\{", @"^\}\s*as const;");
            var list = new List<(string, string)>();
            foreach (Match m in Regex.Matches(body, @"^\s*(?:(\w+)|""([^""]+)"")\s*:\s*""([^""]*)""\s*,?\s*(?://.*)?$", RegexOptions.Multiline))
            {
                string name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                list.Add((name, m.Groups[3].Value));
            }
            // every non-comment, non-blank line must have matched
            int lines = 0;
            foreach (string line in body.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length > 0 && !t.StartsWith("//")) lines++;
            }
            if (lines != list.Count) throw new InvalidDataException($"icons.ts ICONS: {lines} entry lines but {list.Count} parsed");
            return list;
        }

        private static List<(string key, string stem)> ParsePictures(string ts)
        {
            string body = Block(ts, @"const PICTURES\s*:\s*Record<string,\s*string>\s*=\s*\{", @"^\};");
            var list = new List<(string, string)>();
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(body, @"(?:(\w+)|""([^""]+)"")\s*:\s*""([^""]+)"""))
            {
                string key = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                list.Add((key, m.Groups[3].Value));
                map[key] = m.Groups[3].Value;
            }
            foreach (Match m in Regex.Matches(ts, @"PICTURES\.(\w+)\s*=\s*PICTURES(?:\.(\w+)|\[""([^""]+)""\])!?;"))
            {
                string src = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                if (!map.TryGetValue(src, out string stem)) throw new InvalidDataException($"icons.ts: PICTURES alias of unknown key {src}");
                list.Add((m.Groups[1].Value, stem));
                map[m.Groups[1].Value] = stem;
            }
            return list;
        }

        private static List<string> ParseArtKeys(string ts)
        {
            string body = Block(ts, @"const ART\s*:\s*Record<string,\s*string>\s*=\s*\{", @"^\};");
            var list = new List<string>();
            foreach (Match m in Regex.Matches(body, @"^  (?:(\w+)|""([^""]+)"")\s*:", RegexOptions.Multiline))
                list.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            return list;
        }

        // ---------------------------------------------------------------- ModernIcons.cs

        private static string Pascal(string s)
        {
            var sb = new StringBuilder();
            bool up = true;
            foreach (char c in s)
            {
                if (!char.IsLetterOrDigit(c)) { up = true; continue; }
                sb.Append(up ? char.ToUpperInvariant(c) : c);
                up = false;
            }
            if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.ToString();
        }

        private static string Lit(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private static string EmitCs(List<(string name, int index)> icons, List<(string stem, int index)> emblems,
            List<(string key, string stem)> emblemKeys, List<string> names, List<string> artKeys, int firstEmblem, int count)
        {
            var used = new HashSet<string>(StringComparer.Ordinal)
            {
                "IconSize", "IconCount", "FirstEmblem", "EmblemCount", "Count", "Names", "NotIncluded", "NameIndex",
                "EmblemIndex", "Index", "Emblem", "BuildNameIndex", "BuildEmblemIndex", "ModernIcons",
            };
            foreach (var (name, _) in icons)
                if (!used.Add(Pascal(name))) throw new InvalidDataException($"icon '{name}' maps to a duplicate/reserved constant {Pascal(name)}");
            foreach (var (stem, _) in emblems)
                if (!used.Add("Emblem" + Pascal(stem))) throw new InvalidDataException($"emblem '{stem}' maps to a duplicate constant");

            var o = new StringBuilder();
            void L(string s = "") => o.Append(s).Append("\r\n");

            L("// <auto-generated>");
            L("// generated by tools\\modern-icons — do not edit. Rerun the generator when icons.ts gains an icon");
            L("// (tools\\modern-icons\\README.md); it rewrites this file and Client\\Resources\\ModernIcons.Lib together.");
            L("// </auto-generated>");
            L("using System.Collections.Generic;");
            L();
            L("namespace Client.MirScenes.Modern.Core");
            L("{");
            L("    /// <summary>");
            L("    /// Image indices in ModernIcons.Lib (Desktop-Modern-UI-Plan D4).");
            L("    /// Stroke icons (WebClient core/icons.ts ICONS, white on transparent, tint with the sprite colour): the constant is");
            L($"    /// the 1x image ({IconSize} x {IconSize}, stroke 2); the 2x image ({IconSize * 2} x {IconSize * 2}, stroke 4) is constant + 1.");
            L("    /// Emblems (WebClient play/menu-icons/*.png, full colour, native size, one size only): Emblem* constants,");
            L("    /// or <see cref=\"Emblem\"/> with a PICTURES key (window name) or file name.");
            L("    /// </summary>");
            L("    public static class ModernIcons");
            L("    {");
            L($"        public const int IconSize = {IconSize};");
            L($"        public const int IconCount = {icons.Count};");
            L($"        public const int FirstEmblem = {firstEmblem};");
            L($"        public const int EmblemCount = {emblems.Count};");
            L($"        public const int Count = {count};");
            L();
            L("        // stroke icons (1x index; 2x = index + 1)");
            foreach (var (name, index) in icons) L($"        public const int {Pascal(name)} = {index};");
            L();
            L("        // emblems");
            foreach (var (stem, index) in emblems) L($"        public const int Emblem{Pascal(stem)} = {index};");
            L();
            L("        /// <summary>Name of every image by index: icon name, \"name@2x\", \"emblem:file\".</summary>");
            L("        public static readonly string[] Names =");
            L("        {");
            foreach (string n in names) L($"            {Lit(n)},");
            L("        };");
            L();
            L("        /// <summary>ART emblems of icons.ts (SVG with gradients) that are not in the library.</summary>");
            L("        public static readonly string[] NotIncluded =");
            L("        {");
            foreach (string k in artKeys) L($"            {Lit(k)},");
            L("        };");
            L();
            L("        private static readonly Dictionary<string, int> NameIndex = BuildNameIndex();");
            L("        private static readonly Dictionary<string, int> EmblemIndex = BuildEmblemIndex();");
            L();
            L("        /// <summary>Image index of a name from <see cref=\"Names\"/> (an ICONS name gives the 1x image), or -1.</summary>");
            L("        public static int Index(string name)");
            L("        {");
            L("            if (name == null) return -1;");
            L("            return NameIndex.TryGetValue(name, out int i) ? i : -1;");
            L("        }");
            L();
            L("        /// <summary>Image index of an emblem by PICTURES key (\"Game shop\", \"gear\", ...) or file name (\"game-shop\"), or -1.</summary>");
            L("        public static int Emblem(string key)");
            L("        {");
            L("            if (key == null) return -1;");
            L("            return EmblemIndex.TryGetValue(key, out int i) ? i : -1;");
            L("        }");
            L();
            L("        private static Dictionary<string, int> BuildNameIndex()");
            L("        {");
            L("            var d = new Dictionary<string, int>(Names.Length, System.StringComparer.Ordinal);");
            L("            for (int i = 0; i < Names.Length; i++) d[Names[i]] = i;");
            L("            return d;");
            L("        }");
            L();
            L("        private static Dictionary<string, int> BuildEmblemIndex()");
            L("        {");
            L("            var d = new Dictionary<string, int>(System.StringComparer.Ordinal);");
            foreach (var (stem, _) in emblems) L($"            d[{Lit(stem)}] = Emblem{Pascal(stem)};");
            foreach (var (key, stem) in emblemKeys) L($"            d[{Lit(key)}] = Emblem{Pascal(stem)};");
            L("            return d;");
            L("        }");
            L("    }");
            L("}");
            return o.ToString();
        }

        // ---------------------------------------------------------------- verify

        /// <summary>
        /// --verify &lt;lib&gt; [--out &lt;dir&gt;] [index ...]: re-reads the library like the client's MLibrary, decodes every
        /// image, writes the given indices (default: 0, 1 and the last image) as PNGs plus sheet.png (all images on grey).
        /// </summary>
        private static int Verify(string[] args)
        {
            if (args.Length < 2) throw new ArgumentException("usage: --verify <lib> [--out <dir>] [index ...]");
            string lib = args[1];
            string outDir = Opt(args, "--out");
            var picks = new List<int>();
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--out") { i++; continue; }
                picks.Add(int.Parse(args[i], CultureInfo.InvariantCulture));
            }

            var (version, entries, frameCount, decode) = LibReader.Open(File.ReadAllBytes(lib));
            Console.WriteLine($"version {version}, {entries.Length} images, frame count {frameCount}");
            if (picks.Count == 0) picks.AddRange(new[] { 0, 1, entries.Length - 1 });

            var all = new LibImage[entries.Length];
            int opaqueNonWhite = 0;
            for (int i = 0; i < entries.Length; i++)
            {
                all[i] = decode(i);
                if (entries[i].Width == IconSize || entries[i].Width == IconSize * 2)
                {
                    var px = all[i].Pixels;
                    for (int p = 0; p < px.Length; p += 4)
                        if (px[p + 3] != 0 && (px[p] != 255 || px[p + 1] != 255 || px[p + 2] != 255)) opaqueNonWhite++;
                }
            }
            Console.WriteLine($"all {entries.Length} images decoded; non-white visible pixels in icon images: {opaqueNonWhite}");

            if (outDir != null)
            {
                Directory.CreateDirectory(outDir);
                foreach (int i in picks)
                {
                    var e = entries[i];
                    using var bmp = all[i].ToBitmap();
                    string f = Path.Combine(outDir, $"image{i}.png");
                    bmp.Save(f, ImageFormat.Png);
                    Console.WriteLine($"image {i}: {e.Width} x {e.Height} at ({e.X},{e.Y}) -> {f}");
                }
                SaveSheet(all, Path.Combine(outDir, "sheet.png"));
            }
            return 0;
        }

        private static void SaveSheet(LibImage[] all, string file)
        {
            const int cell = 76, cols = 12;
            int rows = (all.Length + cols - 1) / cols;
            using var sheet = new Bitmap(cols * cell, rows * cell, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(sheet))
            {
                g.Clear(Color.FromArgb(255, 40, 44, 52));
                for (int i = 0; i < all.Length; i++)
                {
                    using var bmp = all[i].ToBitmap();
                    g.DrawImageUnscaled(bmp, (i % cols) * cell + 2, (i / cols) * cell + 2);
                }
            }
            sheet.Save(file, ImageFormat.Png);
        }
    }
}
