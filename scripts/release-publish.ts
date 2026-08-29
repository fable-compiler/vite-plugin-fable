import { $ } from "bun";
import { tmpdir } from "node:os";
import path from "node:path";
import {
  type ChangelogRelease,
  latestRelease,
  packageDir,
  packageName,
  repoRoot,
} from "./changelog";

// Publishes the version CHANGELOG.md describes to npm and creates the matching GitHub release.
// Called by .github/workflows/release.yml, which is dispatched by scripts/release-detect.ts.
//
// Everything here is bun except the publish itself. `bun publish` cannot authenticate through the
// workflow's OIDC token (https://github.com/oven-sh/bun/issues/22423), and trusted publishing is
// the whole point, so npm takes the tarball bun packed.

const dryRun: boolean = Bun.argv.includes("--dry-run");
const requested: string | undefined = Bun.argv
  .slice(2)
  .find((argument: string): boolean => !argument.startsWith("--"));

const { version, notes }: ChangelogRelease = await latestRelease();

// The workflow is dispatched with the version release-detect.ts saw. A mismatch means the two ran
// against different commits, which would publish something nobody asked for.
if (requested && requested !== version) {
  throw new Error(`Asked to release ${requested}, but CHANGELOG.md describes ${version}.`);
}

// CHANGELOG.md is the only place a version is written by hand, so the package version is derived
// from it here rather than kept in sync by hand. The edit is never committed: it exists for the
// tarball this run publishes.
await $`bun pm pkg set version=${version}`.cwd(packageDir);

// npm and bun both publish a README and a LICENSE, but only the ones next to the package.json.
for (const name of ["README.md", "LICENSE"]) {
  await Bun.write(path.join(packageDir, name), Bun.file(path.join(repoRoot, name)));
}

// `bun pm pack` runs the package's prepack, which builds the daemon and the plugin, and resolves
// the `catalog:` ranges that npm would otherwise publish verbatim.
const packDirectory: string = path.join(tmpdir(), `${packageName}-${version}`);

await $`bun pm pack --destination ${packDirectory}`.cwd(packageDir);

// The name bun gives the tarball, rather than the one it printed: prepack's build output goes to
// the same stdout, so reading it back is not worth the parsing.
const tarballPath: string = path.join(packDirectory, `${packageName}-${version}.tgz`);

const published: string[] = await $`bun info ${packageName} versions --json`.json();

if (published.includes(version)) {
  console.log(`${packageName}@${version} is already on npm, skipping the publish.`);
} else if (dryRun) {
  console.log(`Dry run: would publish ${packageName}@${version} to npm.`);
  await $`npm publish --dry-run ${tarballPath}`.cwd(repoRoot);
} else {
  console.log(`Publishing ${packageName}@${version} to npm.`);
  // Authentication is the workflow's OIDC token. --provenance is explicit rather than left to npm,
  // which only turns it on by itself once it has decided it is publishing as a trusted publisher.
  await $`npm publish --provenance --access public ${tarballPath}`.cwd(repoRoot);
}

const tag: string = `v${version}`;
const releaseExists: boolean =
  (await $`gh release view ${tag}`.quiet().nothrow().cwd(repoRoot)).exitCode === 0;

if (releaseExists) {
  console.log(`GitHub release ${tag} already exists, leaving it alone.`);
  process.exit(0);
}

const notesFile: string = path.join(tmpdir(), `${tag}-notes.md`);
await Bun.write(notesFile, `${notes}\n`);

// --target pins the tag to the commit this run packed. Without it gh tags whatever the default
// branch points at by the time the release is created, which is not necessarily the same thing.
const target: string = (await $`git rev-parse HEAD`.cwd(repoRoot).text()).trim();

if (dryRun) {
  console.log(
    `Dry run: would create GitHub release ${tag} at ${target} with these notes:\n\n${notes}`,
  );
} else {
  console.log(`Creating GitHub release ${tag} at ${target}.`);
  await $`gh release create ${tag} --target ${target} --title ${version} --notes-file ${notesFile}`.cwd(
    repoRoot,
  );
}
