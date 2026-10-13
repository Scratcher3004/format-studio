# Projects in the web UI

How the web UI finds, opens and switches projects. For the options see [usage](usage.md#web-ui).

## Use cases

| | Project given | Switching |
|---|---|---|
| Command line | Required (`--project`, or the nearest `*.fstudio.yaml` above the current folder) | n/a |
| Local web UI | Optional (`--project`, `--workspace`) | Yes, via the start page and the app bar |
| Hosted web UI (planned) | Never; projects live in the workspace | Required |

## Design

- **Project in the URL.** Every project page is `/p/{id}/...`. The id is the project file name plus a hash of its
  full path (`bedrock-8cbe031d`), so it survives restarts, can be bookmarked and different tabs can show different
  projects. `/` is the start page; with `--project` it redirects to that project, and `/projects` always shows the
  start page.
- **`ProjectStore`** (one per server) knows the projects: the recent list (`recent.json` in the state folder, newest
  first, at most 20), the workspace scan and `--project`. It loads each project once and shares the
  `ProjectService` across connections, so edits and reloads are seen by everyone viewing that project.
- **`ProjectContext`** (one per browser connection) reads the id from the current URL on every access and forwards
  the current project's reloads to the layout and the type list. Pages derive from `ProjectComponent`, which gives
  them `Projects` and `Link("type/Foo")`. The layout shows a warning instead of the page for an unknown id, and gives
  each project its own page instance, so switching projects doesn't keep the previous project's state.
- **New projects** use `ProjectTemplate` in Core, the same template as `format-studio new`.

## Hosted mode (planned, not implemented)

A `--hosted` switch on top of the same model:

- Only projects below `--workspace` are listed and opened. The id becomes the folder path relative to the
  workspace, and paths outside it are refused. There is no recent list and no free path or folder browser.
- New projects are created as folders in the workspace; projects can be uploaded and downloaded as zip files.
- Importer options that are server paths (the Mojang schema zip) need an upload field; until then only import
  sources inside the project folder are allowed.
- Authentication and access control are left to a reverse proxy. Concurrent edits of the same file are last write
  wins, as in the local mode.
