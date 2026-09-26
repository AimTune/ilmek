---
name: pdf
description: >-
  Fill, merge and read PDF forms. Use when the user mentions a PDF,
  a form to fill, or asks to combine documents.
license: Apache-2.0
allowed-tools: Read Bash(python3:*)
compatibility: Needs python3 with pypdf installed.
metadata:
  author: AimTune
  version: "1.2"   # quoted so YAML keeps it a string
---

# PDF skill

Use the bundled helpers instead of reinventing them:

- `scripts/fill.py` fills a form from a JSON payload.
- See `references/forms.md` for the field-naming conventions.

Always confirm the output path with the user before writing.
