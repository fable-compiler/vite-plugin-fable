namespace Fable.Daemon

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
        ErrorNumberText : string
        Message : string
        Range : DiagnosticRange
        Severity : string
        FileName : FullPath
    }

[<RequireQualifiedAccess>]
type ProjectChangedResult =
    | Success of sourceFiles : FullPath array * diagnostics : Diagnostic array * dependentFiles : FullPath array
    | Error of string

[<RequireQualifiedAccess>]
type FilesCompiledResult =
    | Success of compiledFSharpFiles : Map<FullPath, JavaScript>
    | Error of string

[<RequireQualifiedAccess>]
type FileChangedResult =
    | Success of compiledFSharpFiles : Map<FullPath, JavaScript> * diagnostics : Diagnostic array
    | Error of string

type CompileFilesPayload = { FileNames : FullPath array }
