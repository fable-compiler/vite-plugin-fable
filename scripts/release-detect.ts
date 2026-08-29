import { $ } from "bun";
import { type ChangelogRelease, latestRelease, packageName } from "./changelog";

// Decides whether CHANGELOG.md describes a version that is not on npm yet, and dispatches
// .github/workflows/release.yml when it does. Runs on every push to main.
//
// npm is what the release is compared against rather than the git tags, because npm is what a
// publish collides with: a version that is already there cannot be published a second time,
// whatever the tags say.

const { version }: ChangelogRelease = await latestRelease();
const published: string[] = await $`bun info ${packageName} versions --json`.json();

if (published.includes(version)) {
  console.log(`No release needed. ${packageName}@${version} is already on npm.`);
  process.exit(0);
}

const latestPublished: string | undefined = published.toSorted(Bun.semver.order).at(-1);

if (latestPublished && Bun.semver.order(version, latestPublished) < 0) {
  throw new Error(
    `CHANGELOG.md releases ${version}, which is older than ${latestPublished} on npm.`,
  );
}

console.log(`${version} is not on npm yet (latest published is ${latestPublished ?? "nothing"}).`);

if (process.env.CI !== "true") {
  console.log("Not running in CI, so the release workflow is not dispatched.");
  process.exit(0);
}

await $`gh workflow run release.yml --ref main -f version=${version}`;
console.log(`Dispatched release.yml for ${version}.`);
