# Wheel Control Fix

Torch plugin that fixes the Space Engineers wheel control bug where propulsion override gets inverted after entering a cockpit: https://support.keenswh.com/spaceengineers/pc/topic/55881-wheel-override-propulsion-direction-differs-based-on-cockpit-entry-history

## Build

Linux:
```bash
SE_GAME_DIR=$HOME/.steam/steam/steamapps/common/SpaceEngineers TORCH_DIR=$HOME/torch dotnet build -c Release
```

Figure out the right paths for Windows, you probably just need to `SET` them somehow before building instead of putting them in the command line.

Copy `bin/net48/WheelControlFix.dll` and `manifest.xml` to your Torch instance's
`Plugins/WheelControlFix/` folder.

Precompiled binaries are in the releases if that's easier.
