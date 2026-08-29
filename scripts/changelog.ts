import path from "node:path";
import { type Changelog, parser, type Release } from "keep-a-changelog";

export const repoRoot: string = path.resolve(import.meta.dir, "..");
export const packageName: string = "vite-plugin-fable";
export const packageDir: string = path.join(repoRoot, "packages", packageName);

export interface ChangelogRelease {
  /** The version as written in CHANGELOG.md, without a `v` prefix. */
  version: string;
  /** The body of the release section, without its heading. */
  notes: string;
}

/**
 * The most recent released version in CHANGELOG.md. `[Unreleased]` carries no date, which is what
 * distinguishes it from a real release: a version is released by giving it a number and a date.
 */
export async function latestRelease(): Promise<ChangelogRelease> {
  const markdown: string = await Bun.file(path.join(repoRoot, "CHANGELOG.md")).text();
  const changelog: Changelog = parser(markdown);
  const release: Release | undefined = changelog.releases.find((release: Release): boolean =>
    Boolean(release.date && release.version),
  );

  if (!release?.version) {
    throw new Error("CHANGELOG.md has no dated release; nothing can be released from it.");
  }

  const [, ...body]: string[] = release.toString().split("\n");

  return { version: release.version, notes: body.join("\n").trim() };
}
