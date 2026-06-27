# AssetStore

> ⚠️ Prototype. The C# core + CLI for the decentralized Stride Asset Store.
> See the companion **AssetContainer** repository for the registry, schemas and CI.

## Projects

| Project | Description |
|---|---|
| `src/AssetStore.Core` | Pure .NET 10 library: models, JSON-Schema validation, deterministic `AssetData/` hashing, `.csproj` inspection (Stride version + project references), dependency resolution, index building. Reused by the CLI, the CI bot, and the future Blazor app. |
| `src/AssetStore.Cli` | The `assetstore` global tool (`validate`, `build-index`). |
| `src/AssetStore.App` | Blazor WebAssembly storefront (GitHub Pages): browse, search, filter, sort, asset detail with environment-aware install (download .zip / clone / `dotnet add package`). Consumes `index.lock.json` via `AssetStore.Core`. |
| `tests/AssetStore.Core.Tests` | xUnit tests, including an end-to-end build against the example asset repos. |

## CLI usage

```bash
# Validate every registry entry + manifest (schemas, catalog, Stride version, dependencies)
dotnet run --project src/AssetStore.Cli -- validate --container ../AssetContainer

# Generate the aggregated index consumed by the app
dotnet run --project src/AssetStore.Cli -- build-index --container ../AssetContainer
```

By default the workspace (where local asset checkouts live) is the container's parent directory, so
clone the asset repositories next to `AssetContainer`.

## What the core does

- **Validation**: registry entry + manifest against JSON Schema, plus catalog (category/license),
  id/file-name consistency, and `dependencies ⊇ <ProjectReference>`-to-store-assets.
- **Integrity**: pins the resolved git commit and computes a deterministic SHA-256 of `AssetData/`.
- **Stride version**: detected from the `.csproj` (`Stride.* PackageReference`).
- **Dependencies**: transitive resolution by `id` (no version constraint), cycle/missing detection.

## Web app

```bash
# run the storefront locally (detects "local" mode → install will be enabled in the desktop build)
dotnet run --project src/AssetStore.App

# publish the static site (what GitHub Pages serves)
dotnet publish src/AssetStore.App -c Release -o publish
```

The app loads `wwwroot/data/index.lock.json` by default; point `Catalog:IndexUrl` (appsettings) at a
raw GitHub URL once the registry repo is public. Deployment to Pages is automated by
`.github/workflows/deploy-pages.yml`.

## Build & test

```bash
dotnet build
dotnet test
```

## License

MIT. See the AssetContainer `LICENSE.md`.
