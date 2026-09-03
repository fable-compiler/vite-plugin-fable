namespace Fable.Daemon

open System.Text.Json
open System.Text.Json.Serialization
open FSharp.Compiler.CodeAnalysis

type FullPath = string
type Hash = string
type JavaScript = string

type ProjectChangedPayload =
    {
        /// Release or Debug.
        Configuration : string
        /// Absolute path of fsproj.
        Project : FullPath
        /// Absolute path of fable-library. Typically found in the npm modules.
        FableLibrary : FullPath
        /// Which project should be excluded? Used when you are testing a local plugin.
        Exclude : string array
        /// Don't emit JavaScript reflection code.
        NoReflection : bool
    }

type DiagnosticRange =
    {
        StartLine : int
        StartColumn : int
        EndLine : int
        EndColumn : int
    }

type Diagnostic =
    {
        /// The F# error number, `FS0025` and the like. Empty for anything Fable reported itself,
        /// which has no such number.
        ErrorNumberText : string
        Message : string
        Range : DiagnosticRange
        Severity : string
        FileName : FullPath
        /// Which half of the compiler reported this: `FSHARP` for the F# compiler, `FABLE` for
        /// something Fable could not translate. An F# error means the code does not compile; a
        /// Fable one means it compiles but cannot be turned into JavaScript.
        Tag : string
    }

[<RequireQualifiedAccess>]
type ProjectChangedResult =
    | Success of sourceFiles : FullPath array * diagnostics : Diagnostic array * dependentFiles : FullPath array
    | Error of error : string

[<RequireQualifiedAccess>]
type FilesCompiledResult =
    | Success of compiledFSharpFiles : Map<FullPath, JavaScript> * diagnostics : Diagnostic array
    | Error of error : string

[<RequireQualifiedAccess>]
type FileChangedResult =
    | Success of compiledFSharpFiles : Map<FullPath, JavaScript> * diagnostics : Diagnostic array
    | Error of error : string

type CompileFilesPayload = { FileNames : FullPath array }

/// How the types above cross the JSON-RPC boundary.
///
/// These options are the contract: `packages/vite-plugin-fable/src/daemon.ts` decodes exactly what
/// they produce. They live here rather than inline in the server so that the contract test can
/// serialise with the same options the daemon serves with, instead of a copy that agrees with
/// itself.
[<RequireQualifiedAccess>]
module Wire =

    let serializerOptions () : JsonSerializerOptions =
        let options =
            JsonSerializerOptions (PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

        // Named fields, so `fields` is an object keyed by the F# field names rather than an array
        // the other side has to index by position. Reordering a case's fields then renames keys
        // instead of silently changing what `fields[1]` means.
        let jsonFSharpOptions =
            JsonFSharpOptions.Default().WithUnionTagName("case").WithUnionFieldsName("fields").WithUnionNamedFields()

        options.Converters.Add (JsonUnionConverter jsonFSharpOptions)
        options
