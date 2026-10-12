# Building


## The shaders

The mod has two shaders of its own, kept ready made in `src/NaturalLight/Shaders/` and copied into
`GameData/NaturalLight/PluginData/` by the build: `shafts.bundle` (the shafts of sunlight) and
`clean.bundle` (black over the game's pixels that are not numbers). Their text is
`tools/shaderpack/shafts.glsl` and `clean.glsl`. To make them again after a change, with a copy of the
game to take a pattern from:

```bash
python3 tools/shaderpack/make_bundle.py "/path/to/Kerbal Space Program" src/NaturalLight/Shaders/shafts.bundle shafts
python3 tools/shaderpack/make_bundle.py "/path/to/Kerbal Space Program" src/NaturalLight/Shaders/clean.bundle clean
```

That packs them for OpenGL (a Mac, Linux) without the Unity editor: a compiled OpenGL shader is its text
and a list of what it reads. For Direct3D (Windows) all three, with the weather's veil, go into one bundle,
`PluginData/light-windows.bundle`, made on Windows by `python3 tools/unityshaders/windows.py <KSP folder>`
(each turned into HLSL and compiled by Windows' own compiler, without the Unity editor).

## The floodlights' model

The floodlights' searchlight is made by a Blender script, `src/NaturalLight/Model/floodlight.py`, which writes
`floodlight.mesh`, `floodlight_colour.png` and `floodlight_normal.png` into `src/NaturalLight/PluginData/`; the
build copies that folder into `GameData/NaturalLight/PluginData/`. Nothing is downloaded and no model file is kept
by hand. To make it again after a change (with Blender 5.2; the window is never opened):

```bash
blender --background --factory-startup --python src/NaturalLight/Model/floodlight.py -- src/NaturalLight/PluginData
```

`--quick` bakes with few samples and a picture half as wide, to try a change; `--render FOLDER` also draws pictures
of the result, by day and lit, beside a kerbal-sized stand-in. The full bake takes about two minutes.
`./build.sh` builds the mod and installs it into the game; `./build.sh check` only compiles; `./build.sh dist`
builds into `GameData/` here without touching the game. It needs the .NET SDK, a copy of the game (`KSP_DIR=/path`
if it is not where Steam puts it), and `Keystone.dll` from [Keystone](https://github.com/IshiakiZ/ksp-keystone):
the copy installed in the game, a built copy of that repository beside this one, or `KEYSTONE=/path/to/Keystone.dll`.

This repository is made from a workspace that holds several mods side by side; changes arrive here from there.
