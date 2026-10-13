# Inspector Component Strip

Unity Editor-only tool that inserts a small chip strip at the top of each Inspector window.

## Behavior

- A chip is shown per component on the inspected GameObject.
- Click a chip to toggle isolating that component in the Inspector.
- Click multiple chips to show multiple components.
- Double-click any chip to reset and show all components again.
- Locked Inspectors are respected.

## Install (UPM via Git URL)

In Unity: **Window → Package Manager → + → Add package from git URL...**

- `https://github.com/FurrField-Studio/InspectorComponentStrip.git`

## Notes

This tool changes component `hideFlags` at edit time and restores them on selection change, domain reload, and editor quit.

## Known issue

The strip only appears in inspectors showing a GameObject, Component, or prefab root. If you open a new Inspector window without changing your selection, the strip will not show up until you reselect a GameObject.
