# Notes for AI agents

This repository is the **IFS Academy SCORM Editor**, a .NET 8 Blazor Server app (Interactive
Server). It is being merged into another Blazor app (the IFS Academy Content Manager).

Start here:

1. **Merging the editor into another app?** Follow [`docs/LLM-MERGE-PLAYBOOK.md`](docs/LLM-MERGE-PLAYBOOK.md)
   step by step. It lists exact files, exact edits, invariants and checks.
2. **Need to understand how something works?** [`docs/SCORM-Editor-Guide.md`](docs/SCORM-Editor-Guide.md):
   section 6 traces every flow method by method, section 7 describes every file.

Rules that apply to any change in this repo:

- Build with `dotnet build`; run with `dotnet run` and open `/editor`. There is no test project:
  test in the browser (Playwright works) and, for Azure, with Azurite (playbook section 8).
- Keep the invariants in playbook section 3 (no SAS in the browser, launcher courses only overwrite
  their original, ETag check on Azure saves, never delete blobs, save minimal edits never the
  rendered DOM, word check never replaces).
- User-facing text is plain, short English sentences. Keep the existing style.
- The owner wants every change as a pull request, merged to `main` once tested.
