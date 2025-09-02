# PictureChanger Tools (Unity Editor)

A set of Unity Editor utilities to scan, prepare, and bind images for PictureChanger components and Picture-material objects in your project. It provides:

- Scan usages and texture sizes across Prefabs and Scenes.
- Generate resized/compressed copies of source images (long side < N).
- Randomly assign orientation-aware textures to frames in scenes.
- Keep Prefabs up to date on import; optionally rebind Scenes.
- Clean up unreferenced generated/compressed textures.

## Requirements
- Unity Editor 2020+ (tested with Editor API used in scripts)
- Unity Test Framework (optional, for running tests)

## Installation
- Place the  folder in your project (already present in this repo).
- On first use, a config asset is created at .

## Configuration
Edit :
- rootFolder: Base output folder (default: ).
- defaultRandomFolder: Source folder to pick images from.
- compressedFolderName: Subfolder for resized/compressed outputs.
- maxLongSideLessThan: Max long edge for generated textures.
- verboseLogging: Enable additional logs.
- autoRebindScenesOnImport: If true, scenes are auto opened/saved on texture import; otherwise, scene rebinds are queued.

## Menu Commands
- Tools/PictureChanger/Scan usages and sizes
  - Scans Prefabs and Scenes, writes report to .
  - Creates per-size folders under  as needed.
- Tools/PictureChanger/Random assign VRChat images (resize, scenes)
  - Generates resized textures into .
  - Randomly assigns orientation-matching textures per scene. Shows progress and allows cancel.
- Tools/PictureChanger/Clean unreferenced compressed images
  - Deletes compressed textures not referenced by any PictureChanger in Prefabs/Scenes.
- Tools/PictureChanger/Rebind scenes for pending imports
  - Applies queued rebinds for imported textures without auto-saving scenes on every import.

## Safety and UX
- Long operations show progress bars and allow cancellation.
- Potentially destructive operations prompt for confirmation.
- Import-time processing updates Prefabs and queues Scene updates by default.

## Tests
- Basic EditMode tests for pure helpers live under  (if present).
- Run via Unity Test Runner (Window -> General -> Test Runner).

## Development Notes
- Editor code is split into focused files: tools, postprocessor, rebind queue, and config.
- IO operations have basic exception handling to avoid full-stop failures.
- Further refactoring can extract additional helpers or add more granular logging levels.
