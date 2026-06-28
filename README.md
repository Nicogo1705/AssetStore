# AssetStore

> ⚠️ **Unofficial** prototype — a community-built, **decentralized asset indexer** for the
> [Stride](https://stride3d.net) game engine. **Not affiliated with, endorsed by, or operated by
> Stride / the .NET Foundation.** Built so it *could* be adopted/integrated by the Stride community
> later (config-only) if wanted — a possibility, not a plan. See the companion **AssetContainer**
> repository for the registry, schemas and CI.

This solution is the C# code: a reusable core, a CLI, a web storefront, a shared UI, and a
cross-platform desktop client. Assets are not hosted here — each asset lives in its author's own
public Git repo; this just indexes and installs them.

## Projects

| Project | Description |
|---|---|
| `src/AssetStore.Core` | Pure .NET 10 library: models, JSON-Schema validation, deterministic `AssetData/` hashing, `.csproj`/`.sln` inspection (Stride version + project references), dependency resolution, git client, index building. Reused everywhere. |
| `src/AssetStore.Cli` | The `assetstore` global tool: `validate`, `build-index` (`--incremental`, `--stars`, `--source git`), `refresh-stars`. |
| `src/AssetStore.UI` | Shared Razor class library (components, pages, services) used by both hosts. |
| `src/AssetStore.App` | **Blazor WebAssembly** storefront for GitHub Pages: browse / search / filter / sort, asset detail, and the **publish** wizard (fork + PR via a GitHub token). No local access → no install. |
| `src/AssetStore.Desktop` | **Blazor Server** local app (Windows / Linux / macOS) that opens the browser and has full filesystem + git access: **install** an asset (clone + `<ProjectReference>`, with dependencies) and the **Installed** manager (up-to-date / update). |
| `tests/AssetStore.Core.Tests` | xUnit tests (incl. an end-to-end build against the example asset repos). |

`AssetStore.App` = the online vitrine; `AssetStore.Desktop` = the local power tool. Both share
`AssetStore.UI`.

## CLI usage

```bash
# Validate every registry entry + manifest (schemas, catalog, Stride version, dependencies)
dotnet run --project src/AssetStore.Cli -- validate --container ../AssetContainer --source git

# Generate the aggregated index consumed by the apps
dotnet run --project src/AssetStore.Cli -- build-index --container ../AssetContainer --source git --stars

# Cheap incremental refresh (only re-fetch assets whose ref moved) — used by the daily CI job
dotnet run --project src/AssetStore.Cli -- build-index --container ../AssetContainer --source git --incremental --stars
```

`--source local` (default) reads asset checkouts sitting next to `AssetContainer`; `--source git`
clones them.

## Run the apps

```bash
# Online storefront (WASM)
dotnet run --project src/AssetStore.App

# Desktop client (opens http://localhost:5111 in your browser, enables install)
dotnet run --project src/AssetStore.Desktop
```

## Configuration

- **Registry location** (`Registry` section → `RegistryOptions`): `Owner` / `Repo` / `BaseBranch`.
  Defaults to `Nicogo1705/AssetContainer/main`; change it (config only, no code) to point at another
  org — e.g. a Stride community org, should the project ever be adopted.
- **Catalog index** (`Catalog:IndexUrl`): where the WASM app fetches `index.lock.json`.

## What the core does

- **Validation**: registry entry + manifest against JSON Schema (with `format` assertions), catalog
  (category/license), id/file-name consistency, `https://`-only repo URLs.
- **Integrity**: pins the resolved git commit + a deterministic, OS-independent SHA-256 of `AssetData/`;
  the installer re-verifies the hash after cloning.
- **Security**: git is invoked with no shell, `ext`/`file` transports disabled, and option-like
  arguments rejected; clone folder names are sanitized against path traversal.
- **Stride version**: detected from the `.csproj` (`Stride.* PackageReference`).
- **Dependencies**: transitive resolution by `id` (cycle/missing detection), auto-derived from
  `<ProjectReference>`.

## Build & test

```bash
dotnet build
dotnet test
```

## License

MIT. See [LICENSE.md](LICENSE.md).
