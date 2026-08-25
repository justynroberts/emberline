using Emberline.Camera;
using Emberline.Core.Documents;
using Emberline.Core.Machines;
using Emberline.Devices;
using Emberline.Materials;
using Emberline.Transport;

namespace Emberline.Plugins;

public sealed record RegisteredDriver(string DriverId, string Description, string Plugin, Func<MachineProfile, ILaserDevice> Factory);
public sealed record RegisteredTransport(string Scheme, string Description, string Plugin, Func<string, ITransport> Factory);
public sealed record RegisteredCamera(string Kind, string Description, string Plugin, Func<string, ICameraSource> Factory);
public sealed record RegisteredImporter(string Name, IReadOnlyList<string> Extensions, string Plugin, Func<string, IReadOnlyList<Shape>> Import);

/// <summary>
/// Everything the loaded plugins have contributed.
///
/// Built-ins always win. A plugin that registers `.svg` or the `grbl` driver does
/// not get to replace the tested implementation — the clash is reported and the
/// registration refused, because silently shadowing the G-code path with
/// third-party code is not a trade anybody agreed to.
/// </summary>
public sealed class PluginRegistry : IPluginRegistry
{
    private readonly List<string> _messages = [];
    private string _currentPlugin = "unknown";

    /// <summary>Names that plugins may not take, because Emberline already provides them.</summary>
    public HashSet<string> ReservedDriverIds { get; } = new(StringComparer.OrdinalIgnoreCase) { "grbl", "blazex" };

    public HashSet<string> ReservedExtensions { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ".svg", ".dxf", ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp",
        ".nc", ".gcode", ".gc", ".tap", ".ngc",
    };

    public List<RegisteredDriver> Drivers { get; } = [];
    public List<RegisteredTransport> Transports { get; } = [];
    public List<RegisteredCamera> Cameras { get; } = [];
    public List<RegisteredImporter> Importers { get; } = [];
    public List<MaterialProfile> Materials { get; } = [];

    public IReadOnlyList<string> Messages => _messages;

    internal void BeginPlugin(string name) => _currentPlugin = name;

    /// <summary>Attribute the following registrations to a named plugin. For tests and hosts.</summary>
    public void BeginPluginForTest(string name) => BeginPlugin(name);

    public void AddDeviceDriver(string driverId, string description, Func<MachineProfile, ILaserDevice> factory)
    {
        if (string.IsNullOrWhiteSpace(driverId)) return;

        if (ReservedDriverIds.Contains(driverId))
        {
            Log($"refused to register the driver '{driverId}' — that name belongs to Emberline.");
            return;
        }

        if (Drivers.Any(d => string.Equals(d.DriverId, driverId, StringComparison.OrdinalIgnoreCase)))
        {
            Log($"refused to register the driver '{driverId}' — another plugin registered it first.");
            return;
        }

        Drivers.Add(new RegisteredDriver(driverId, description, _currentPlugin, factory));
    }

    public void AddTransport(string scheme, string description, Func<string, ITransport> factory)
    {
        if (string.IsNullOrWhiteSpace(scheme)) return;

        if (Transports.Any(t => string.Equals(t.Scheme, scheme, StringComparison.OrdinalIgnoreCase)))
        {
            Log($"refused to register the transport '{scheme}' — another plugin registered it first.");
            return;
        }

        Transports.Add(new RegisteredTransport(scheme, description, _currentPlugin, factory));
    }

    public void AddCamera(string kind, string description, Func<string, ICameraSource> factory)
    {
        if (string.IsNullOrWhiteSpace(kind)) return;

        if (Cameras.Any(c => string.Equals(c.Kind, kind, StringComparison.OrdinalIgnoreCase)))
        {
            Log($"refused to register the camera '{kind}' — another plugin registered it first.");
            return;
        }

        Cameras.Add(new RegisteredCamera(kind, description, _currentPlugin, factory));
    }

    public void AddImporter(string name, IReadOnlyList<string> extensions, Func<string, IReadOnlyList<Shape>> importer)
    {
        var usable = extensions
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .Where(e =>
            {
                if (!ReservedExtensions.Contains(e)) return true;
                Log($"refused '{e}' from the importer '{name}' — Emberline already reads that format.");
                return false;
            })
            .Where(e =>
            {
                if (!Importers.Any(i => i.Extensions.Contains(e, StringComparer.OrdinalIgnoreCase))) return true;
                Log($"refused '{e}' from the importer '{name}' — another plugin claimed it first.");
                return false;
            })
            .ToList();

        if (usable.Count == 0) return;

        Importers.Add(new RegisteredImporter(name, usable, _currentPlugin, importer));
    }

    public void AddMaterials(IReadOnlyList<MaterialProfile> materials)
    {
        foreach (var material in materials) Materials.Add(material with { IsBuiltIn = false });
    }

    public void Log(string message) => _messages.Add($"[{_currentPlugin}] {message}");

    /// <summary>Find an importer that claims this file, if any.</summary>
    public RegisteredImporter? ImporterFor(string path)
    {
        var extension = Path.GetExtension(path);
        return string.IsNullOrEmpty(extension)
            ? null
            : Importers.FirstOrDefault(i => i.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
    }

    public RegisteredDriver? DriverFor(string driverId) =>
        Drivers.FirstOrDefault(d => string.Equals(d.DriverId, driverId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every extension any plugin can open, for the file picker.</summary>
    public IReadOnlyList<string> AllImporterExtensions =>
        [.. Importers.SelectMany(i => i.Extensions).Distinct(StringComparer.OrdinalIgnoreCase)];
}
