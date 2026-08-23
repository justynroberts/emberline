using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenBurn.Core.Documents;
using OpenBurn.Devices;
using OpenBurn.Plugins;

namespace OpenBurn.App.ViewModels;

/// <summary>
/// Plugin loading.
///
/// Off unless the user has turned it on, because a plugin is ordinary code running
/// in this process with their permissions and there is no sandbox. When it is on,
/// every loaded plugin is named, every failure is reported, and a plugin cannot
/// displace a built-in importer or driver.
/// </summary>
public sealed partial class MainViewModel
{
    public PluginRegistry Plugins { get; } = new();

    [ObservableProperty]
    private PluginLoadReport _pluginReport = PluginLoadReport.Empty;

    public bool PluginsEnabled => Settings.PluginsEnabled;

    public string PluginSummary => PluginReport.Summary;

    public IReadOnlyList<LoadedPlugin> LoadedPlugins => PluginReport.Loaded;

    /// <summary>Load plugins and wire what they registered into the rest of the application.</summary>
    private void LoadPlugins()
    {
        if (!Settings.PluginsEnabled) return;

        PluginReport = PluginHost.Load(Plugins, enabled: true);

        foreach (var plugin in PluginReport.Loaded)
        {
            Console.AppendInfo($"Plugin loaded: {plugin.Name} {plugin.Version} — {plugin.Description}");
        }

        foreach (var failure in PluginReport.Failures)
        {
            Console.AppendError($"Plugin failed to load — {failure}");
        }

        foreach (var message in PluginReport.Messages) Console.AppendInfo($"Plugin: {message}");

        // Drivers and transports reach the device layer as lookups, so that layer
        // never has to know plugins exist.
        DeviceFactory.PluginDriverLookup = id => Plugins.DriverFor(id)?.Factory;
        DeviceFactory.PluginTransportLookup = scheme =>
            Plugins.Transports.FirstOrDefault(t => string.Equals(t.Scheme, scheme, StringComparison.OrdinalIgnoreCase))?.Factory;

        foreach (var material in Plugins.Materials) MaterialLibrary.Add(material);

        if (Plugins.Materials.Count > 0)
        {
            Console.AppendInfo($"Plugins contributed {Plugins.Materials.Count} material profile(s).");
            OnPropertyChanged(nameof(AvailableMaterials));
        }

        OnPropertyChanged(nameof(PluginSummary));
        OnPropertyChanged(nameof(LoadedPlugins));
    }

    /// <summary>Try the plugin importers for a file OpenBurn does not recognise.</summary>
    private bool TryPluginImport(string path)
    {
        if (Plugins.ImporterFor(path) is not { } importer) return false;

        try
        {
            var shapes = importer.Import(path);
            if (shapes.Count == 0)
            {
                Console.AppendInfo($"'{importer.Name}' opened {Path.GetFileName(path)} but produced no geometry.");
                return true;
            }

            EditDocument($"Import {Path.GetFileName(path)}", () =>
            {
                Selection.Clear();
                foreach (var shape in shapes)
                {
                    PlaceOnBed(shape);
                    Design.AddShape(shape, SelectedLayer?.Layer);
                    Selection.Add(shape);
                }
            });

            Console.AppendInfo($"Imported {shapes.Count} shape(s) from {Path.GetFileName(path)} using '{importer.Name}' " +
                               $"(plugin: {importer.Plugin}).");
            return true;
        }
        catch (Exception ex)
        {
            // A plugin importer that throws is the plugin's problem.
            Console.AppendError($"'{importer.Name}' failed on {Path.GetFileName(path)}: {ex.Message}");
            return true;
        }
    }

    [RelayCommand]
    private void TogglePlugins()
    {
        Settings = Settings with { PluginsEnabled = !Settings.PluginsEnabled };
        Settings.Save();

        Console.AppendInfo(Settings.PluginsEnabled
            ? "Plugin loading is on. Restart OpenBurn to load them. A plugin runs with your permissions and is not sandboxed — only load ones you trust."
            : "Plugin loading is off. Restart OpenBurn to unload them.");

        OnPropertyChanged(nameof(PluginsEnabled));
    }
}
