# Writing an OpenBurn plugin

A plugin is a .NET class library that references `OpenBurn.Plugins` and exposes one
type implementing `IOpenBurnPlugin`:

```csharp
using OpenBurn.Plugins;

public sealed class MyPlugin : IOpenBurnPlugin
{
    public string Name => "Acme laser support";
    public string Version => "1.0.0";
    public string Description => "Adds the Acme network protocol.";

    public void Register(IPluginRegistry registry)
    {
        registry.AddTransport("acme", "Acme over UDP", address => new AcmeTransport(address));
        registry.AddDeviceDriver("acme", "Acme controller", profile => new AcmeDevice(profile));
    }
}
```

Build it and drop the output in `plugins/` inside the OpenBurn application data
folder — either as a loose DLL, or as `plugins/MyPlugin/MyPlugin.dll` with its
dependencies beside it. Enable plugin loading in the app, and restart.

## What you can add

| | |
|---|---|
| `AddDeviceDriver` | A machine driver, selected by a profile's `driverId` |
| `AddTransport` | A bus, protocol or bridge |
| `AddCamera` | A source of frames |
| `AddImporter` | A file format |
| `AddMaterials` | Material profiles |

## What you cannot do

You cannot replace a built-in. Registering `.svg`, or the `grbl` driver, is refused
and reported — silently shadowing the tested G-code path with third-party code is
not a trade anybody agreed to.

`Register` is called once, on the startup thread. Do not block in it, open a
device, or touch the network: a plugin that does turns a slow network into an
application that will not start.

## Honesty about the risk

A plugin is ordinary code running in the OpenBurn process with your permissions.
There is no sandbox. Loading is off by default, every loaded plugin is named in
the interface, and a plugin that throws while registering is skipped rather than
taking the application down — but none of that makes it safe to run a plugin you
have no reason to trust, on a machine that can start a fire.
