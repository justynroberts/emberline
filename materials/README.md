# Material profiles

Built-in materials ship inside `OpenBurn.Materials`. This folder is for exported
and shared profiles — a JSON array of `MaterialProfile` objects, the same shape the
in-app **Export materials** command produces.

Settings are always tied to the wattage they were measured at. OpenBurn will
rescale a profile to a different machine automatically and tells you it has done
so, but the only trustworthy numbers are the ones you measured yourself. Use
**Machine → Generate test grid** and spend ten minutes on an offcut.
