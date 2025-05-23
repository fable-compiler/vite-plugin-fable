import { $ } from "bun";

// Updates the package.json version according to latest release from CHANGELOG.md
// https://www.npmjs.com/package/keep-a-changelog#cli

const version = await $`bunx changelog --latest-release`.text();

const packageVersion = await $`npm info vite-plugin-fable version`.text();

if (version === packageVersion) {
  console.log("version is already up to date, do not publish");
  process.exit(1);
}

await $`npm version ${version.trim()}`;

console.log(`Updated package.json version to ${version.trim()}`);
console.log(`Run 'git push origin main --tags' after publishing to push the new version tag.`);