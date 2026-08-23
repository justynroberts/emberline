using OpenBurn.Camera;
using OpenBurn.Core.Documents;
using OpenBurn.Core.Machines;
using OpenBurn.Devices;
using OpenBurn.Materials;
using OpenBurn.Transport;

namespace OpenBurn.Plugins;

/// <summary>
/// What a plugin can add to OpenBurn.
///
/// Deliberately a registration surface rather than a set of hooks the application
/// calls into: a plugin declares what it provides once, at load, and OpenBurn
/// decides when to use it. That keeps a badly-behaved plugin out of the job
/// engine's hot paths, where it could stall a running machine.
/// </summary>
public interface IPluginRegistry
{
    /// <summary>A machine driver, selected by a profile's <c>driverId</c>.</summary>
    void AddDeviceDriver(string driverId, string description, Func<MachineProfile, ILaserDevice> factory);

    /// <summary>A way of talking to a machine — a bus, a protocol, a bridge.</summary>
    void AddTransport(string scheme, string description, Func<string, ITransport> factory);

    /// <summary>A source of camera frames.</summary>
    void AddCamera(string kind, string description, Func<string, ICameraSource> factory);

    /// <summary>
    /// A file format OpenBurn can open. Extensions include the dot and are matched
    /// case-insensitively; a plugin cannot displace a built-in importer.
    /// </summary>
    void AddImporter(string name, IReadOnlyList<string> extensions, Func<string, IReadOnlyList<Shape>> importer);

    /// <summary>Extra material profiles, merged into the library.</summary>
    void AddMaterials(IReadOnlyList<MaterialProfile> materials);

    /// <summary>Report something the user should see. Plugins should not write to the console directly.</summary>
    void Log(string message);
}

/// <summary>
/// A plugin. One implementation per assembly is the normal case; more are allowed.
/// </summary>
public interface IOpenBurnPlugin
{
    string Name { get; }
    string Version { get; }
    string Description { get; }

    /// <summary>
    /// Declare everything this plugin provides.
    ///
    /// Called once at load, on the startup thread. It must not block, open a
    /// device, or touch the network — a plugin that does turns a slow network into
    /// an application that will not start.
    /// </summary>
    void Register(IPluginRegistry registry);
}

/// <summary>Marks the plugin's own metadata for the plugin list. Optional.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class OpenBurnPluginAttribute(string name) : Attribute
{
    public string Name { get; } = name;
    public string? Author { get; init; }
    public string? Url { get; init; }
}
