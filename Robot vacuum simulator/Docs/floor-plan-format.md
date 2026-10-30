# Floor plan file format

Floor plans are plain JSON. You can write or edit one in any text editor and bring it in with **Import** on the level editor's floor plan list, or take one out with **Export…** on a floor plan's menu. The app doesn't need to be running to read or write these files.

- **In the Unity Editor:** Import and Export open file dialogs.
- **In a Windows build:** Export writes to `%USERPROFILE%\AppData\LocalLow\DefaultCompany\Robot vacuum simulator\Exports`. Import reads every `.json` file in the `Import` folder beside it, then moves each one it read into `Import\Imported`.

The library's own copies live in that same data folder under `Levels`. They use this format too, minus the room covering names that exported files add.

All lengths are in **metres**. The x axis points right and the y axis points up. Angles are in degrees, measured counter-clockwise.

## Example

```json
{
  "version": 1,
  "name": "Two rooms",
  "rooms": [
    {
      "name": "Kitchen",
      "outline": [ { "x": 0, "y": 0 }, { "x": 4, "y": 0 }, { "x": 4, "y": 3 }, { "x": 0, "y": 3 } ],
      "floor": "Tile",
      "wallEdges": [ true, true, true, true ]
    },
    {
      "name": "Living room",
      "outline": [ { "x": 4, "y": 0 }, { "x": 9, "y": 0 }, { "x": 9, "y": 3 }, { "x": 4, "y": 3 } ],
      "floor": "Low Carpet"
    }
  ],
  "doorways": [ { "roomA": 0, "roomB": 1, "center": { "x": 4, "y": 1.5 }, "width": 0.9 } ],
  "wallStrokes": [],
  "obstacles": [
    { "name": "Sofa", "kind": 0, "center": { "x": 7, "y": 2.3 }, "size": { "x": 2, "y": 0.9 }, "rotation": 0, "blocksVacuum": true },
    { "name": "Table", "kind": 2, "center": { "x": 6, "y": 1 }, "size": { "x": 1.2, "y": 0.8 }, "rotation": 15, "blocksVacuum": false }
  ],
  "wallThickness": 0.12,
  "robotSpawn": { "x": 1, "y": 1 }
}
```

## Fields

| Field | Meaning |
|---|---|
| `version` | Format version. Currently `1`. |
| `name` | The name shown in the library. A clash on import gets a number added, such as "Kitchen 2". |
| `rooms[].name` | The room's name, shown in the editor and in per-room coverage. |
| `rooms[].outline` | The floor polygon's corners, in order. The polygon can be concave and either winding works, but its sides must not cross. Needs at least 3 corners. |
| `rooms[].floor` | The floor covering by name, matched against the app's coverings without regard to case: Hardwood, Tile, Low Carpet, High Carpet, Area Rug. It takes priority over `floorIndex` when given. |
| `rooms[].floorIndex` | The floor covering by position in the app's palette. Used when `floor` is missing or doesn't match any covering. |
| `rooms[].wallEdges` | One flag per side: `false` means that side has no wall. Side *i* runs from corner *i* to corner *i + 1*. Missing flags default to `true`. |
| `doorways[]` | A gap cut into any wall within about 12 cm of `center`, `width` metres wide. `roomA` and `roomB` are the room indices it links. |
| `wallStrokes[]` | Free-standing walls from `a` to `b`. |
| `obstacles[].kind` | 0 Sofa, 1 Bed, 2 Table, 3 Chair, 4 Cabinet, 5 Box, 6 Cat. A cat moves during the run and never blocks floor. |
| `obstacles[].blocksVacuum` | `true` means the vacuum can't enter it, and the floor under it is left out of coverage. `false` means the vacuum drives underneath. |
| `obstacles[].size`, `rotation` | Width and depth in metres, and the rotation in degrees. |
| `wallThickness` | Wall thickness in metres. Walls are centred on room sides. |
| `robotSpawn` | Where the vacuum starts. It must be inside a room. |

## Checks on import and before a run

On import, a file with no valid room is rejected. Before any run, the app warns about these, and you can still go ahead:

- The vacuum starts outside every room.
- A room can't be reached through doorways, so it will register 0% coverage.
- An outside wall has a gap wide enough for the vacuum to drive out of.
