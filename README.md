# Wheel Control Fix

Torch plugin that fixes the Space Engineers wheel control bug where propulsion override gets inverted after entering a cockpit: https://support.keenswh.com/spaceengineers/pc/topic/55881-wheel-override-propulsion-direction-differs-based-on-cockpit-entry-history

## Build

```bash
dotnet build
```

Copy `bin/net48/WheelControlFix.dll` and `manifest.xml` to your Torch instance's
`Plugins/WheelControlFix/` folder.
