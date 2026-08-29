import { spawn } from "node:child_process";
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

const currentDir = path.dirname(fileURLToPath(import.meta.url));

// The plugin is emitted to `dist/`, the daemon is published to `bin/` at the package root.
const daemonAssembly = path.join(currentDir, "..", "bin", "Fable.Daemon.dll");

/**
 * A discriminated union case as it arrives over JSON-RPC. The wire format is positional: `fields`
 * mirrors the declaration order of the F# case. Private to this module so the rest of the plugin
 * never indexes into it — see roadmap item 8.
 */
interface FSharpDiscriminatedUnion {
  case: string;
  fields: any[];
}

function describeFailure(reason: string): string {
  return `${reason}\nThe Fable daemon could not be started. vite-plugin-fable needs the .NET 10 SDK on your PATH; check that \`dotnet --version\` works.`;
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
  const dotnetProcess = spawn("dotnet", [daemonAssembly, "--stdio"], { stdio: "pipe" });
  const endpoint = new JSONRPCEndpoint(dotnetProcess.stdin, dotnetProcess.stdout);
  let disposed = false;

  // stderr is piped, so it has to be drained: once the pipe buffer fills the daemon blocks on write.
  dotnetProcess.stderr.on("data", (data: Buffer) => {
    const message = data.toString().trimEnd();
    if (message) {
      logger.error(message);
    }
  });

  /**
   * Rejects once the daemon fails to start or exits unexpectedly. Raced against every request so a
   * dead daemon surfaces an error instead of leaving the caller awaiting a reply that never comes.
   */
  const failed = new Promise<never>((_resolve, reject) => {
    dotnetProcess.once("error", (error) => {
      reject(new Error(describeFailure(`Could not spawn \`dotnet\`: ${error.message}`)));
    });
    dotnetProcess.once("exit", (code, signal) => {
      if (disposed) return;
      const how = signal ? `signal ${signal}` : `exit code ${code}`;
      reject(new Error(describeFailure(`The Fable daemon stopped unexpectedly (${how}).`)));
    });
  });
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
      const fields = unwrap(await send("fable/project-changed", request));
      return { sourceFiles: fields[0], diagnostics: fields[1], dependentFiles: fields[2] };
    },

    async initialCompile(): Promise<Record<string, string>> {
      const fields = unwrap(await send("fable/initial-compile"));
      return fields[0];
    },

    async compile(files: string[]): Promise<CompileResult> {
      const result = await send("fable/compile", { fileNames: files });
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
