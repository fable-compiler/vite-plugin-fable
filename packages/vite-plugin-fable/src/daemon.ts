import { spawn } from "node:child_process";
import type { ChildProcessWithoutNullStreams } from "node:child_process";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { JSONRPCEndpoint } from "ts-lsp-client";
import type {
  CompileResult,
  DaemonLogger,
  FableDaemon,
  ProjectFileData,
  ProjectRequest,
} from "./types.js";

const currentDir: string = path.dirname(fileURLToPath(import.meta.url));

// The plugin is emitted to `dist/`, the daemon is published to `bin/` at the package root.
const daemonAssembly: string = path.join(currentDir, "..", "bin", "Fable.Daemon.dll");

/**
 * A discriminated union case as it arrives over JSON-RPC. The wire format is positional: `fields`
 * mirrors the declaration order of the F# case, so reordering the fields of a case in `Types.fs`
 * changes what every index below means, with no compile error on either side. Private to this
 * module so that hazard stays in one file rather than spreading to every caller.
 */
interface FSharpDiscriminatedUnion {
  case: string;
  fields: any[];
}

/** The SDK advice only fits a daemon that never started; a mid-session crash is a different bug. */
function describeStartFailure(reason: string): string {
  return `${reason}\nvite-plugin-fable needs the .NET 10 SDK on your PATH; check that \`dotnet --version\` works.`;
}

/**
 * Spawns `Fable.Daemon` and returns a handle to it.
 *
 * The returned daemon owns the child process for its whole lifetime: callers never see the process,
 * the JSON-RPC endpoint, or the positional wire format. Requests reject if the daemon dies rather
 * than awaiting a reply that will never arrive, so a missing .NET SDK surfaces as an error instead
 * of a hang. Call {@link FableDaemon.dispose} exactly once when finished.
 */
export function startDaemon(logger: DaemonLogger): FableDaemon {
  const dotnetProcess: ChildProcessWithoutNullStreams = spawn(
    "dotnet",
    [daemonAssembly, "--stdio"],
    {
      stdio: "pipe",
    },
  );
  const endpoint: JSONRPCEndpoint = new JSONRPCEndpoint(dotnetProcess.stdin, dotnetProcess.stdout);
  let disposed = false;

  // stderr is piped, so it has to be drained: once the pipe buffer fills the daemon blocks on write.
  dotnetProcess.stderr.on("data", (data: Buffer) => {
    const message: string = data.toString().trimEnd();
    if (message) {
      logger.error(message);
    }
  });

  /**
   * Rejects once the daemon fails to start or exits unexpectedly. Raced against every request so a
   * dead daemon surfaces an error instead of leaving the caller awaiting a reply that never comes.
   */
  const failed: Promise<never> = new Promise<never>(
    (_resolve: unknown, reject: (reason: Error) => void) => {
      dotnetProcess.once("error", (error: Error) => {
        reject(new Error(describeStartFailure(`Could not spawn \`dotnet\`: ${error.message}`)));
      });
      dotnetProcess.once("exit", (code: number | null, signal: NodeJS.Signals | null) => {
        if (disposed) return;
        const how: string = signal ? `signal ${signal}` : `exit code ${code}`;
        // Whatever went wrong, it was not the SDK: the daemon had already started.
        reject(
          new Error(
            `The Fable daemon stopped unexpectedly (${how}). Any output it wrote is above this.`,
          ),
        );
      });
    },
  );
  // Nothing awaits this until it is raced, so keep Node from flagging an unhandled rejection.
  failed.catch(() => {});

  async function send(method: string, params?: unknown): Promise<FSharpDiscriminatedUnion> {
    if (disposed) {
      throw new Error("The Fable daemon is not running.");
    }
    return Promise.race([
      endpoint.send(method, params) as Promise<FSharpDiscriminatedUnion>,
      failed,
    ]);
  }

  /** Unwraps a `Success` case, turning any other case into an error. */
  function unwrap(result: FSharpDiscriminatedUnion): any[] {
    if (result.case !== "Success") {
      throw new Error(result.fields[0] || "Unknown error occurred");
    }
    return result.fields;
  }

  return {
    async projectChanged(request: ProjectRequest): Promise<ProjectFileData> {
      const fields: any[] = unwrap(await send("fable/project-changed", request));
      return { sourceFiles: fields[0], diagnostics: fields[1], dependentFiles: fields[2] };
    },

    async initialCompile(): Promise<Record<string, string>> {
      const fields: any[] = unwrap(await send("fable/initial-compile"));
      return fields[0];
    },

    async compile(files: string[]): Promise<CompileResult> {
      const result: FSharpDiscriminatedUnion = await send("fable/compile", { fileNames: files });
      if (result.case !== "Success" || !result.fields || result.fields.length === 0) {
        throw new Error(result.fields?.[0] || "Unknown error occurred");
      }
      return { compiledFiles: result.fields[0], diagnostics: result.fields[1] ?? [] };
    },

    dispose(): void {
      if (disposed) return;
      disposed = true;
      dotnetProcess.kill();
    },
  };
}
