import { $ } from "bun";
import path from "node:path";

// Updates the package.json version of vite-plugin-fable according to the latest release in CHANGELOG.md
// https://www.npmjs.com/package/keep-a-changelog#cli

const repoRoot: string = path.resolve(import.meta.dir, "..");
const changelog: string = path.join(repoRoot, "CHANGELOG.md");
const packageDir: string = path.join(repoRoot, "packages/vite-plugin-fable");

const version: string = (
  await $`bunx changelog --latest-release --file ${changelog}`.text()
).trim();

const packageVersion: string = (await $`npm info vite-plugin-fable version`.text()).trim();

if (version === packageVersion) {
  process.exit(0);
}

await $`npm version ${version}`.cwd(packageDir);
