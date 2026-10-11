namespace Server.WebAdmin.MapArt
{
    /// <summary>
    /// Map library index -> path under the client Data folder: the same table as Client/MirGraphics/MLibrary.cs
    /// (<c>Libraries.MapLibs</c>, 400 slots). Keep the two in step.
    /// </summary>
    internal static class MapLibCatalog
    {
        public const int Size = 400;

        private static readonly string[] Paths = Build();

        /// <summary>Relative path without ".Lib" (e.g. @"Map\WemadeMir2\Tiles"), or null for an unused slot.</summary>
        public static string RelativePath(int index) => index >= 0 && index < Paths.Length ? Paths[index] : null;

        private static string[] Build()
        {
            var p = new string[Size];

            // wemade mir2 (0-99)
            p[0] = @"Map\WemadeMir2\Tiles";
            p[1] = @"Map\WemadeMir2\Smtiles";
            p[2] = @"Map\WemadeMir2\Objects";
            for (var i = 2; i < 28; i++) p[i + 1] = @"Map\WemadeMir2\Objects" + i;
            p[90] = @"Map\WemadeMir2\Objects_32bit";

            // shanda mir2 (100-199)
            p[100] = @"Map\ShandaMir2\Tiles";
            for (var i = 1; i < 10; i++) p[100 + i] = @"Map\ShandaMir2\Tiles" + (i + 1);
            p[110] = @"Map\ShandaMir2\SmTiles";
            for (var i = 1; i < 10; i++) p[110 + i] = @"Map\ShandaMir2\SmTiles" + (i + 1);
            p[120] = @"Map\ShandaMir2\Objects";
            for (var i = 1; i < 31; i++) p[120 + i] = @"Map\ShandaMir2\Objects" + (i + 1);
            p[190] = @"Map\ShandaMir2\AniTiles1";

            // wemade mir3 (200-299)
            string[] state = { "", "wood\\", "sand\\", "snow\\", "forest\\" };
            string[] mir3 = { "Tilesc", "Tiles30c", "Tiles5c", "Smtilesc", "Housesc", "Cliffsc", "Dungeonsc", "Innersc", "Furnituresc", "Wallsc", "smObjectsc", "Animationsc", "Object1c", "Object2c" };
            for (var i = 0; i < state.Length; i++)
                for (var j = 0; j < mir3.Length; j++)
                    p[200 + i * 15 + j] = @"Map\WemadeMir3\" + state[i] + mir3[j];

            // shanda mir3 (300-399)
            string[] state3 = { "", "wood", "sand", "snow", "forest" };
            for (var i = 0; i < state3.Length; i++)
                for (var j = 0; j < mir3.Length; j++)
                    p[300 + i * 15 + j] = @"Map\ShandaMir3\" + mir3[j] + state3[i];

            return p;
        }
    }
}
