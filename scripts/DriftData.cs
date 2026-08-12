// DriftData.cs — static, no scene dependency. The Drift star-map data
// (batch 16), embedded verbatim from the design handover: 26 systems, 80
// planets. Do not edit names without a matching update to the aux-display
// Map view, which renders these strings directly.

namespace ColdOrbit.SimCore;

public static class DriftData
{
    public record Planet(string Name);

    public record StarSystem(
        string Id,          // single letter A–Z
        string StarName,
        string StarType,
        Planet[] Planets);  // empty array for Xelgrave

    public static readonly StarSystem[] Systems = new[]
    {
        new StarSystem("A", "Aurivane",  "B-type",             new[] { new Planet("Ashra"), new Planet("Avonis"), new Planet("Ardal") }),
        new StarSystem("B", "Belkarra",  "Binary",             new[] { new Planet("Bexar"), new Planet("Boreth"), new Planet("Brynhal"), new Planet("Belsara") }),
        new StarSystem("C", "Cathrax",   "M-type",             new[] { new Planet("Caldris"), new Planet("Corvax") }),
        new StarSystem("D", "Duskane",   "Red giant",          new[] { new Planet("Dorral") }),
        new StarSystem("E", "Eshalon",   "G-type",             new[] { new Planet("Esben"), new Planet("Eirlys"), new Planet("Endra"), new Planet("Elmara"), new Planet("Ethran"), new Planet("Evanth") }),
        new StarSystem("F", "Favrenn",   "F-type",             new[] { new Planet("Faelric"), new Planet("Ferrun"), new Planet("Farsa"), new Planet("Fenvale") }),
        new StarSystem("G", "Gethryn",   "White dwarf",        new[] { new Planet("Grael"), new Planet("Gorvane"), new Planet("Ghesta"), new Planet("Grendal"), new Planet("Golstrav") }),
        new StarSystem("H", "Hessarin",  "K-type",             new[] { new Planet("Hessik"), new Planet("Halvorn"), new Planet("Hendra"), new Planet("Hurath"), new Planet("Hovash") }),
        new StarSystem("I", "Ivrenna",   "M-type",             new[] { new Planet("Isvard"), new Planet("Ithran"), new Planet("Ilmara"), new Planet("Ivorn") }),
        new StarSystem("J", "Jovendra",  "A-type",             new[] { new Planet("Jareth"), new Planet("Jendra"), new Planet("Joras") }),
        new StarSystem("K", "Kerath",    "K-type",             new[] { new Planet("Kael") }),
        new StarSystem("L", "Loreth",    "Brown dwarf",        new[] { new Planet("Loran") }),
        new StarSystem("M", "Mireth",    "K-type",             new[] { new Planet("Maldrin"), new Planet("Myrrhen"), new Planet("Movane"), new Planet("Marresh") }),
        new StarSystem("N", "Nyxaros",   "Pulsar",             new[] { new Planet("Nyxa"), new Planet("Noross") }),
        new StarSystem("O", "Osmerin",   "F-type",             new[] { new Planet("Osric"), new Planet("Orvane"), new Planet("Othel"), new Planet("Ostrava") }),
        new StarSystem("P", "Perlan",    "M-type",             new[] { new Planet("Perrek"), new Planet("Pyrhen"), new Planet("Pelvos") }),
        new StarSystem("Q", "Quorven",   "K-type",             new[] { new Planet("Quel"), new Planet("Quoraith"), new Planet("Quenna") }),
        new StarSystem("R", "Rovash",    "M-type",             new[] { new Planet("Rethis"), new Planet("Rovane") }),
        new StarSystem("S", "Savarin",   "G-type",             new[] { new Planet("Sevrin"), new Planet("Sorvane"), new Planet("Sethral"), new Planet("Shaldris"), new Planet("Sarnoth") }),
        new StarSystem("T", "Threnval",  "A-type",             new[] { new Planet("Tessin"), new Planet("Thara") }),
        new StarSystem("U", "Undrasi",   "M-type",             new[] { new Planet("Ulvane"), new Planet("Ushira"), new Planet("Undrel"), new Planet("Uvaris") }),
        new StarSystem("V", "Vantheris", "A-type",             new[] { new Planet("Vessik"), new Planet("Varrow"), new Planet("Vondrel"), new Planet("Vashera"), new Planet("Vireth") }),
        new StarSystem("W", "Wyvane",    "B-type",             new[] { new Planet("Wrenna"), new Planet("Wyndel") }),
        new StarSystem("X", "Xelgrave",  "Black hole remnant", System.Array.Empty<Planet>()),
        new StarSystem("Y", "Yrendal",   "M-type",             new[] { new Planet("Yrengar"), new Planet("Yolvane"), new Planet("Ysendra") }),
        new StarSystem("Z", "Zerath",    "K-type",             new[] { new Planet("Zaelin") }),
    };

    // Index lookup: 'A'=0, 'B'=1, ... 'Z'=25
    public static int SystemIndex(string id) => id[0] - 'A';
    public static StarSystem System(string id) => Systems[SystemIndex(id)];

    // A single navigable destination: either a star (PlanetIndex == -1) or a
    // planet within a star's system. `Name` is the display string; `SystemId`
    // is always the containing system.
    public record Destination(string SystemId, int PlanetIndex, string Name)
    {
        public bool IsStar => PlanetIndex < 0;
    }

    // Canonical flat destination list shared by the ControlPanels prev/next
    // cycler and the Admin flat picker so both agree on ordering: all 26 stars
    // in A–Z order first, then every planet grouped by system in A–Z order.
    public static readonly Destination[] Destinations = BuildDestinations();

    private static Destination[] BuildDestinations()
    {
        var list = new System.Collections.Generic.List<Destination>();
        foreach (var s in Systems)
            list.Add(new Destination(s.Id, -1, s.StarName));
        foreach (var s in Systems)
            for (int p = 0; p < s.Planets.Length; p++)
                list.Add(new Destination(s.Id, p, s.Planets[p].Name));
        return list.ToArray();
    }

    // Index into Destinations for a given selection, or 0 (first star) if the
    // selection can't be found.
    public static int DestinationIndexOf(string systemId, int planetIndex)
    {
        for (int i = 0; i < Destinations.Length; i++)
        {
            var d = Destinations[i];
            if (d.SystemId == systemId && d.PlanetIndex == planetIndex) return i;
        }
        return 0;
    }

    // Star positions in drift_star_map_v2.svg viewBox units, indexed A–Z to
    // match Systems above. Taken from the per-system label anchors in the SVG;
    // labels sit a fixed offset below their star, so the offset cancels out of
    // any star-to-star delta and positions are used as-is.
    private static readonly (float X, float Y)[] MapPositions =
    {
        ( 90, 184), (240, 114), (420, 102), (580, 196), (130, 315),
        (340, 195), (500, 250), (610, 364), ( 70, 422), (260, 277),
        (390, 323), (540, 137), (600, 492), (100, 530), (220, 405),
        (350, 457), (480, 406), (590, 562), (160, 565), (300, 347),
        (440, 532), ( 75, 269), (520, 498), (380, 563), (250, 479),
        (470, 114),
    };

    // Map units per AU. The chart spans ~660 units corner to corner; this
    // scale puts the furthest pair of systems at roughly 19 AU, matching the
    // magnitude of the alphabetical placeholder model it replaces.
    private const float MapUnitsPerAu = 36f;

    // Straight-line distance between two systems, in AU, from the real star
    // chart. Same-system returns 0.
    public static float DistanceAu(string fromSystemId, string toSystemId)
    {
        var a = MapPositions[SystemIndex(fromSystemId)];
        var b = MapPositions[SystemIndex(toSystemId)];
        float dx = b.X - a.X;
        float dy = b.Y - a.Y;
        return System.MathF.Sqrt(dx * dx + dy * dy) / MapUnitsPerAu;
    }
}
