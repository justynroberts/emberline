using System.Text.Json;
using System.Text.Json.Serialization;
using Emberline.Core.Documents;

namespace Emberline.Materials;

/// <summary>
/// The material database.
///
/// Built-in profiles are a starting point, not gospel: every machine, lens and
/// batch of plywood is different. They exist so a new user gets a burn that is
/// roughly right on their first attempt instead of a scorched workpiece, and every
/// one of them points at the test grid as the real answer.
/// </summary>
public sealed class MaterialLibrary
{
    private readonly List<MaterialProfile> _profiles = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public IReadOnlyList<MaterialProfile> Profiles => _profiles;

    public static MaterialLibrary CreateDefault()
    {
        var library = new MaterialLibrary();
        library._profiles.AddRange(BuiltIn);
        return library;
    }

    public IEnumerable<string> Categories => _profiles.Select(p => p.Category).Distinct().Order();

    public IEnumerable<MaterialProfile> InCategory(string category) =>
        _profiles.Where(p => string.Equals(p.Category, category, StringComparison.OrdinalIgnoreCase))
                 .OrderBy(p => p.Name).ThenBy(p => p.ThicknessMm);

    /// <summary>
    /// The best match for a machine's wattage: an exact-band profile if one exists,
    /// otherwise the nearest band rescaled, so the library is useful on any machine
    /// rather than only the ones it was measured on.
    /// </summary>
    public MaterialProfile? Find(string name, double thicknessMm, double laserWatts)
    {
        var candidates = _profiles
            .Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) &&
                        Math.Abs(p.ThicknessMm - thicknessMm) < 0.51)
            .ToList();

        if (candidates.Count == 0) return null;

        var exact = candidates.FirstOrDefault(p => Math.Abs(p.LaserWatts - laserWatts) < 0.51);
        if (exact is not null) return exact;

        var nearest = candidates.MinBy(p => Math.Abs(p.LaserWatts - laserWatts))!;
        return nearest.ScaleTo(laserWatts);
    }

    public IEnumerable<MaterialProfile> Search(string term)
    {
        if (string.IsNullOrWhiteSpace(term)) return _profiles;
        return _profiles.Where(p =>
            p.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            p.Category.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            (p.Notes?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    public void Add(MaterialProfile profile) => _profiles.Add(profile);

    public bool Remove(string id) => _profiles.RemoveAll(p => p.Id == id) > 0;

    public void Replace(MaterialProfile profile)
    {
        var index = _profiles.FindIndex(p => p.Id == profile.Id);
        if (index >= 0) _profiles[index] = profile;
        else _profiles.Add(profile);
    }

    /// <summary>Apply a material's settings to a layer.</summary>
    public static void ApplyTo(Layer layer, MaterialProfile material)
    {
        var op = material.For(layer.Operation) ?? material.Operations.FirstOrDefault();
        if (op is null) return;

        layer.SpeedMmMin = op.SpeedMmMin;
        layer.PowerPercent = op.PowerPercent;
        layer.Passes = op.Passes;
        layer.LineIntervalMm = op.LineIntervalMm;
        layer.AirAssist = op.AirAssist;
    }

    public string ToJson() => JsonSerializer.Serialize(_profiles.Where(p => !p.IsBuiltIn).ToList(), JsonOptions);

    public void LoadUserProfiles(string json)
    {
        var loaded = JsonSerializer.Deserialize<List<MaterialProfile>>(json, JsonOptions);
        if (loaded is null) return;
        foreach (var p in loaded) Replace(p with { IsBuiltIn = false });
    }

    public async Task SaveAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, ToJson()).ConfigureAwait(false);
    }

    public async Task LoadAsync(string path)
    {
        if (!File.Exists(path)) return;
        LoadUserProfiles(await File.ReadAllTextAsync(path).ConfigureAwait(false));
    }

    private static MaterialOperation Op(OperationKind kind, double speed, double power, int passes = 1,
                                        double interval = 0.1, bool air = false, string? notes = null) =>
        new()
        {
            Operation = kind,
            SpeedMmMin = speed,
            PowerPercent = power,
            Passes = passes,
            LineIntervalMm = interval,
            AirAssist = air,
            Notes = notes,
        };

    private static MaterialProfile Profile(string category, string name, double thickness, double watts,
                                           IReadOnlyList<MaterialOperation> ops, string? notes = null, string? hazard = null) =>
        new()
        {
            Id = $"{category}-{name}-{thickness:0.#}-{watts:0.#}".ToLowerInvariant().Replace(' ', '-'),
            Category = category,
            Name = name,
            ThicknessMm = thickness,
            LaserWatts = watts,
            Operations = ops,
            Notes = notes,
            IsBuiltIn = true,
            Hazard = hazard,
        };

    /// <summary>
    /// Built-in starting points, measured on 10 W diode machines unless stated.
    /// Conservative by design: under-burning costs a minute, over-burning costs the
    /// workpiece.
    /// </summary>
    public static readonly IReadOnlyList<MaterialProfile> BuiltIn =
    [
        // ---- Wood -------------------------------------------------------
        Profile("Wood", "Plywood", 3, 10,
        [
            Op(OperationKind.Engrave, 3000, 20, interval: 0.08),
            Op(OperationKind.Fill, 2500, 30, interval: 0.1),
            Op(OperationKind.Score, 1200, 35),
            Op(OperationKind.Cut, 250, 100, passes: 3, air: true),
        ], "Birch ply varies a lot with glue line. If a pass stalls on the glue, add a pass rather than more power."),

        Profile("Wood", "Plywood", 6, 10,
        [
            Op(OperationKind.Engrave, 3000, 20, interval: 0.08),
            Op(OperationKind.Cut, 150, 100, passes: 6, air: true),
        ], "6 mm is at the limit of a 10 W diode. Expect a tapered edge and heavy charring."),

        Profile("Wood", "Basswood", 3, 10,
        [
            Op(OperationKind.Engrave, 3500, 15, interval: 0.08),
            Op(OperationKind.Fill, 3000, 22, interval: 0.1),
            Op(OperationKind.Cut, 350, 95, passes: 2, air: true),
        ], "Light and even — the best wood for photo engraving on a diode."),

        Profile("Wood", "MDF", 3, 10,
        [
            Op(OperationKind.Engrave, 3000, 25, interval: 0.08),
            Op(OperationKind.Cut, 200, 100, passes: 4, air: true),
        ], "Cuts cleanly but smells strongly and leaves a lot of residue. Use air assist and extraction."),

        Profile("Wood", "Bamboo", 3, 10,
        [
            Op(OperationKind.Engrave, 2500, 30, interval: 0.08),
            Op(OperationKind.Cut, 200, 100, passes: 4, air: true),
        ], "Dense and inconsistent along the grain. Engraves with excellent contrast."),

        // ---- Acrylic ----------------------------------------------------
        Profile("Acrylic", "Black acrylic", 3, 10,
        [
            Op(OperationKind.Engrave, 2000, 35, interval: 0.08),
            Op(OperationKind.Cut, 120, 100, passes: 4, air: true),
        ], "Cast acrylic frosts white when engraved; extruded goes clear and dull. Cast is worth the extra cost."),

        Profile("Acrylic", "Clear acrylic", 3, 10,
        [
            Op(OperationKind.Cut, 100, 100, passes: 6, air: true),
        ], "A 10 W diode barely sees clear acrylic — the beam passes straight through. Mask the surface or paint the back, or use a CO₂ laser.",
           "Never cut polycarbonate by mistake: it burns, yellows and releases harmful fumes."),

        // ---- Metal ------------------------------------------------------
        Profile("Metal", "Anodised aluminium", 0, 10,
        [
            Op(OperationKind.Engrave, 1000, 80, interval: 0.06,
               notes: "Greyscale rather than dithered: the anodising ablates progressively, so it holds real tone."),
            Op(OperationKind.Fill, 800, 90, interval: 0.05),
        ], "Marks by removing the dye, not the metal. Black anodising gives the best contrast. No air assist — it cools the surface."),

        Profile("Metal", "Painted metal", 0, 10,
        [
            Op(OperationKind.Engrave, 1500, 70, interval: 0.06),
            Op(OperationKind.Fill, 1200, 85, interval: 0.06),
        ], "Removes the paint to reveal the metal beneath. Powder coat needs more power than spray."),

        Profile("Metal", "Stainless steel with marking spray", 0, 10,
        [
            Op(OperationKind.Engrave, 300, 100, passes: 2, interval: 0.05),
        ], "Needs a marking compound such as CerMark or a molybdenum spray. Bare stainless will not mark on a diode."),

        // ---- Slate, stone, glass ----------------------------------------
        Profile("Slate", "Slate coaster", 0, 10,
        [
            Op(OperationKind.Engrave, 2500, 70, interval: 0.08,
               notes: "Greyscale works well; slate has a genuinely continuous tonal response."),
            Op(OperationKind.Fill, 2000, 80, interval: 0.08),
        ], "Wipe with a damp cloth afterwards to lift the dust and reveal the contrast."),

        Profile("Glass", "Glass", 0, 10,
        [
            Op(OperationKind.Engrave, 1500, 60, interval: 0.08),
        ], "Frosts the surface by micro-fracturing. A coat of dish soap or wet paper evens out the result and stops chipping.",
           "Glass can shatter under thermal shock. Keep power moderate and never engrave tempered glass."),

        // ---- Leather, card, fabric --------------------------------------
        Profile("Leather", "Veg-tan leather", 2, 10,
        [
            Op(OperationKind.Engrave, 3000, 25, interval: 0.08,
               notes: "Jarvis dithering flatters leather grain more than Floyd–Steinberg."),
            Op(OperationKind.Cut, 400, 90, passes: 2, air: true),
        ], "Real veg-tan only. Chrome-tanned leather releases hexavalent chromium.",
           "Do not laser chrome-tanned leather — the fumes are toxic. If you do not know which it is, do not cut it."),

        Profile("Paper", "Card 300 gsm", 0.3, 10,
        [
            Op(OperationKind.Score, 3000, 15),
            Op(OperationKind.Cut, 800, 45, air: true),
        ], "Paper catches fire easily. Keep air assist on and never leave it unattended."),

        Profile("Fabric", "Cotton canvas", 0.5, 10,
        [
            Op(OperationKind.Engrave, 4000, 12, interval: 0.1),
            Op(OperationKind.Cut, 1200, 60, air: true),
        ], "Natural fibres only. Synthetics melt rather than cut and can release harmful fumes.",
           "Never laser PVC, vinyl or anything containing chlorine — it produces hydrogen chloride, which will damage both you and the machine."),

        Profile("Cork", "Cork sheet", 3, 10,
        [
            Op(OperationKind.Engrave, 3000, 20, interval: 0.1),
            Op(OperationKind.Cut, 500, 85, passes: 2, air: true),
        ], "Engraves beautifully with very little power. Watch for flare-ups on cut passes."),

        // ---- A 40 W CO₂ band, so the scaling logic has something to work with ----
        Profile("Wood", "Plywood", 3, 40,
        [
            Op(OperationKind.Engrave, 6000, 15, interval: 0.08),
            Op(OperationKind.Cut, 900, 55, air: true),
        ], "Typical 40 W CO₂ settings — a single clean cut pass."),

        Profile("Acrylic", "Clear acrylic", 3, 40,
        [
            Op(OperationKind.Cut, 600, 60,
               notes: "Turn air assist DOWN for acrylic on CO₂ — too much air frosts the flame-polished edge."),
        ], "CO₂ cuts clear acrylic with a beautifully polished edge, which a diode cannot do at all."),
    ];
}
