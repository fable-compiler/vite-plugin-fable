import { Logger } from "vite";
import { ChildProcessWithoutNullStreams } from "node:child_process";
import { Subscription } from "rxjs";
import { JSONRPCEndpoint } from "ts-lsp-client";

// Represents a generic F# discriminated union case with associated fields.
export interface FSharpDiscriminatedUnion {
  case: string; // The name of the case (mirroring the F# case name)
  fields: any[]; // The fields associated with the case
}

// Represents options for an F# project.
export interface FSharpProjectOptions {
  sourceFiles: string[]; // List of source files in the project
}

// Defines a range within a file, used for diagnostics or annotations.
export interface DiagnosticRange {
  startLine: number; // The start line of the diagnostic range
  startColumn: number; // The start column of the diagnostic range
  endLine: number; // The end line of the diagnostic range
  endColumn: number; // The end column of the diagnostic range
}

// Describes a diagnostic message, typically an error or warning, within a file.
export interface Diagnostic {
  errorNumberText: string; // The error number or identifier text
  message: string; // The descriptive diagnostic message
  range: DiagnosticRange; // The range within the file where the diagnostic applies
  severity: string; // The severity of the diagnostic (e.g., error, warning)
  fileName: string; // The name of the file containing the diagnostic
}

export interface PluginOptions {
  fsproj?: string; // Optional: The main fsproj to load
  jsx?: "transform" | "preserve" | "automatic" | null; // Optional: Apply JSX transformation after Fable compilation
  noReflection?: boolean; // Optional: Pass noReflection value to Fable.Compiler
  exclude?: string[]; // Optional: Pass exclude to Fable.Compiler
}

export interface PluginState {
  config: PluginOptions;
  logger: Logger;
  dotnetProcess: ChildProcessWithoutNullStreams | null;
  endpoint: JSONRPCEndpoint | null;
  compilableFiles: Map<string, string>;
  sourceFiles: Set<string>;
  fsproj: string | null;
  configuration: string;
  dependentFiles: Set<string>;
  pendingChanges: Subscription | null;
  hotPromiseWithResolvers: PromiseWithResolvers<Array<Diagnostic>>;
  isBuild: boolean;
}

// Represents an event where an F# file has changed.
export interface FSharpFileChanged {
  type: "FSharpFileChanged"; // Discriminator for FSharpFileChanged event type
  file: string; // The F# file that changed
}

// Represents an event where a project file has changed.
export interface ProjectFileChanged {
  type: "ProjectFileChanged"; // Discriminator for ProjectFileChanged event type
  file: string; // The project file that changed
}

// Type that represents the possible hook events. Acts as a discriminated union in TypeScript.
export type HookEvent = FSharpFileChanged | ProjectFileChanged;

// Represents the state of pending changes.
export interface PendingChangesState {
  projectChanged: boolean; // Indicates whether the project changed
  fsharpFiles: Set<string>; // Set of changed F# files
  projectFiles: Set<string>; // Set of changed project files
}

export interface ProjectFileData {
  sourceFiles: string[];
  diagnostics: Diagnostic[];
  dependentFiles: string[];
}
