# README

This folder contains fixture projects to test `vite-plugin-fable`.

## Vitest

Project can be called from root, using `bun test` with vitest workspaces defined in `/vitest.config.ts`.

## Bun workspaces

Projects use direct dependency to the local plugin using bun workspaces to `/src/vite-plugin-fable`.

### Root package.json

```json
"workspaces": ["src/*", "tests/*"],
```

### Fixture package.json

```json
"devDependencies": {
  "vite-plugin-fable": "workspace:*",
}
```

## Directory.Packages.props

Manages NuGet dependencies for the fixture project. We need one `Directory.Packages.props` file in this folder to avoid loading the root `Directory.Packages.props` file. 

The projects still try to load `Fable.Compiler` (i have no idea why), therefore we remove it from the fixture projects? 

```xml
<PackageReference Remove="Fable.Compiler" />
```

