using System.Reflection;
using OpenBurn.Camera;
using OpenBurn.Core.Documents;
using OpenBurn.Core.Machines;
using OpenBurn.Devices;
using OpenBurn.Materials;
using OpenBurn.Plugins;
using OpenBurn.Transport;
using Xunit;

namespace OpenBurn.Plugins.Tests;

/// <summary>A plugin that registers one of everything.</summary>
public sealed class GoodPlugin : IOpenBurnPlugin
{
    public string Name => "Test plugin";
    public string Version => "1.0.0";
    public string Description => "Registers one of everything.";

    public void Register(IPluginRegistry registry)
    {
        registry.AddDeviceDriver("acme", "Acme controller", profile => new GrblDevice(profile));
        registry.AddTransport("acme", "Acme over UDP", address => new TcpTransport(address, 9999));
        registry.AddCamera("acme-cam", "Acme camera", address => new FileCameraSource(address));
        registry.AddImporter("Acme parts", [".acme", "acme2"], _ => [PathShape.Rectangle(10, 10)]);
        registry.AddMaterials([new MaterialProfile
        {
            Category = "Acme",
            Name = "Unobtainium",
            LaserWatts = 10,
            Operations = [new MaterialOperation { Operation = OperationKind.Cut, SpeedMmMin = 100, PowerPercent = 100 }],
        }]);
    }
}

/// <summary>A plugin that tries to take names OpenBurn already owns.</summary>
public sealed class GreedyPlugin : IOpenBurnPlugin
{
    public string Name => "Greedy plugin";
    public string Version => "0.1";
    public string Description => "Tries to replace the built-ins.";

    public void Register(IPluginRegistry registry)
    {
        registry.AddDeviceDriver("grbl", "my own GRBL", profile => new GrblDevice(profile));
        registry.AddImporter("My SVG", [".svg", ".mine"], _ => []);
    }
}

/// <summary>A plugin that throws while registering.</summary>
public sealed class BrokenPlugin : IOpenBurnPlugin
{
    public string Name => "Broken plugin";
    public string Version => "0.0";
    public string Description => "Throws on registration.";

    public void Register(IPluginRegistry registry) => throw new InvalidOperationException("nope");
}

public class PluginRegistryTests
{
    private static PluginRegistry RegisterAll()
    {
        var registry = new PluginRegistry();
        PluginHost.RegisterFrom(typeof(GoodPlugin).Assembly, registry, "tests");
        return registry;
    }

    [Fact]
    public void DiscoversEveryPluginInAnAssembly()
    {
        var registry = new PluginRegistry();
        var loaded = PluginHost.RegisterFrom(typeof(GoodPlugin).Assembly, registry, "tests");

        Assert.Contains(loaded, p => p.Name == "Test plugin");
        Assert.Contains(loaded, p => p.Name == "Greedy plugin");
        Assert.Equal("1.0.0", loaded.First(p => p.Name == "Test plugin").Version);
    }

    [Fact]
    public void RegistersDriversTransportsCamerasImportersAndMaterials()
    {
        var registry = RegisterAll();

        Assert.Contains(registry.Drivers, d => d.DriverId == "acme");
        Assert.Contains(registry.Transports, t => t.Scheme == "acme");
        Assert.Contains(registry.Cameras, c => c.Kind == "acme-cam");
        Assert.Contains(registry.Importers, i => i.Name == "Acme parts");
        Assert.Contains(registry.Materials, m => m.Name == "Unobtainium");
    }

    [Fact]
    public void ExtensionsAreNormalisedToIncludeTheDot()
    {
        var registry = RegisterAll();
        var importer = registry.Importers.First(i => i.Name == "Acme parts");

        Assert.Contains(".acme", importer.Extensions);
        Assert.Contains(".acme2", importer.Extensions);
    }

    [Fact]
    public void APluginCannotReplaceABuiltInImporter()
    {
        // Silently shadowing the tested SVG path with third-party code is not a
        // trade anybody agreed to.
        var registry = RegisterAll();

        Assert.DoesNotContain(registry.Importers, i => i.Extensions.Contains(".svg"));
        Assert.Contains(registry.Messages, m => m.Contains(".svg", StringComparison.Ordinal) &&
                                                m.Contains("refused", StringComparison.OrdinalIgnoreCase));

        // Its other, non-clashing extension still registers.
        Assert.Contains(registry.Importers, i => i.Extensions.Contains(".mine"));
    }

    [Fact]
    public void APluginCannotReplaceABuiltInDriver()
    {
        var registry = RegisterAll();

        Assert.DoesNotContain(registry.Drivers, d => string.Equals(d.DriverId, "grbl", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(registry.Messages, m => m.Contains("grbl", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TwoPluginsCannotClaimTheSameName()
    {
        var registry = new PluginRegistry();

        registry.BeginPluginForTest("first");
        registry.AddTransport("shared", "first", a => new TcpTransport(a));

        registry.BeginPluginForTest("second");
        registry.AddTransport("shared", "second", a => new TcpTransport(a));

        Assert.Single(registry.Transports);
        Assert.Equal("first", registry.Transports[0].Description);
        Assert.Contains(registry.Messages, m => m.Contains("registered it first", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void APluginThatThrowsIsSkippedRatherThanFatal()
    {
        var registry = new PluginRegistry();
        var loaded = PluginHost.RegisterFrom(typeof(BrokenPlugin).Assembly, registry, "tests");

        Assert.DoesNotContain(loaded, p => p.Name == "Broken plugin");
        Assert.Contains(registry.Messages, m => m.Contains("failed during registration", StringComparison.OrdinalIgnoreCase));

        // The others still loaded.
        Assert.Contains(loaded, p => p.Name == "Test plugin");
    }

    [Fact]
    public void ImporterLookupMatchesByExtensionCaseInsensitively()
    {
        var registry = RegisterAll();

        Assert.NotNull(registry.ImporterFor("/tmp/part.ACME"));
        Assert.NotNull(registry.ImporterFor("/tmp/part.acme2"));
        Assert.Null(registry.ImporterFor("/tmp/part.svg"));
        Assert.Null(registry.ImporterFor("/tmp/noextension"));
    }

    [Fact]
    public void APluginImporterActuallyProducesShapes()
    {
        var registry = RegisterAll();
        var importer = registry.ImporterFor("thing.acme");

        Assert.NotNull(importer);
        var shapes = importer!.Import("thing.acme");
        Assert.Single(shapes);
        Assert.Equal(10, shapes[0].Bounds.Width, 3);
    }

    [Fact]
    public void PluginMaterialsAreNotMarkedAsBuiltIn()
    {
        var registry = RegisterAll();
        var material = registry.Materials.First(m => m.Name == "Unobtainium");

        Assert.False(material.IsBuiltIn);
    }

    [Fact]
    public void MessagesNameThePluginResponsible()
    {
        var registry = RegisterAll();
        Assert.All(registry.Messages, m => Assert.StartsWith("[", m, StringComparison.Ordinal));
    }

    [Fact]
    public void LoadingIsOffUnlessAskedFor()
    {
        var registry = new PluginRegistry();
        var report = PluginHost.Load(registry, directory: Path.GetTempPath(), enabled: false);

        Assert.False(report.AnythingLoaded);
        Assert.Empty(report.Failures);
    }

    [Fact]
    public void AMissingPluginFolderIsNotAnError()
    {
        var registry = new PluginRegistry();
        var report = PluginHost.Load(registry, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        Assert.False(report.AnythingLoaded);
        Assert.Empty(report.Failures);
        Assert.Equal("No plugins installed.", report.Summary);
    }

    [Fact]
    public void AnAssemblyThatIsNotAPluginIsReportedNotIgnored()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openburn-plugin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        // Any DLL that is not a plugin — the Core assembly will do.
        var source = typeof(PathShape).Assembly.Location;
        var target = Path.Combine(folder, Path.GetFileName(source));
        File.Copy(source, target, overwrite: true);

        try
        {
            var registry = new PluginRegistry();
            var report = PluginHost.Load(registry, folder);

            Assert.False(report.AnythingLoaded);
            Assert.Contains(report.Failures, f => f.Contains("IOpenBurnPlugin", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

/// <summary>
/// What loading a plugin does to the file it came from.
/// </summary>
public class PluginFileLockTests
{
    [Fact]
    public void LoadingAPluginDoesNotLockItsFile()
    {
        // LoadFromAssemblyPath holds the file open for the life of the process.
        // On Unix that is invisible, because an open file can still be deleted;
        // on Windows it locks the DLL, so a plugin cannot be updated or removed
        // while OpenBurn is running and the operator gets "access denied" with
        // nothing to explain it. This can only fail on Windows, which is exactly
        // why it has to exist — CI found it after a Mac never could.
        var folder = Path.Combine(Path.GetTempPath(), "openburn-plugin-lock", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        var source = typeof(PluginHost).Assembly.Location;
        var target = Path.Combine(folder, Path.GetFileName(source));
        File.Copy(source, target, overwrite: true);

        try
        {
            PluginHost.Load(new PluginRegistry(), folder);

            // The point: the file must still be replaceable and removable.
            File.Delete(target);
            Assert.False(File.Exists(target));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
