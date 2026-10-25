---
name: gemini
description: Overwrite gemini.txt with problem description and relevant files for Gemini analysis
---

# /gemini skill

When invoked, overwrite gemini.txt in the project root with:
1. A clear problem description (use any text provided after the command to focus it)
2. Relevant source files, each preceded by its filename/header
3. Current state of relevant code

## Behavior

- If user provides text after /gemini, use it to focus the problem description
- Include relevant files with file path as header
- Overwrite gemini.txt completely

## Relevant files to include

- src/WorldGen.App/Preview3D/PreviewCamera.cs (full)
- src/WorldGen.App/Preview3D/Terrain3DHost.cs (key sections + context)
- Any other files directly related to the problem
