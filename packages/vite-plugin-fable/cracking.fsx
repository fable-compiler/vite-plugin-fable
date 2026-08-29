#!/usr/bin/env -S dotnet fsi
// Scratch script to try out project cracking without going through the Vite plugin.
// Run `bun run postinstall` (or `dotnet publish Fable.Daemon -o ./bin`) first so `./bin` exists.
// Usage: ./cracking.fsx path/to/MyProject.fsproj (defaults to the sample project)
#I "./bin"
#r "Fable.AST"
#r "Fable.Compiler"
#r "Fable.Daemon"
#r "./bin/FSharp.Compiler.Service.dll"
#r "./bin/Microsoft.Extensions.Logging.Abstractions.dll"

open System.IO
open Microsoft.Extensions.Logging
open Fable.Compiler
open Fable.Compiler.Util
open Fable.Compiler.ProjectCracker
open Fable.Daemon

fsi.AddPrinter (fun (x : ProjectOptionsResponse) ->
    $"ProjectOptionsResponse: %i{x.ProjectOptions.Length} options, %i{x.ProjectReferences.Length} references, %s{x.TargetFramework.Value}, %s{x.OutputType.Value}"
)

let fsproj =
    let lastArg = Array.last fsi.CommandLineArgs

    if lastArg.EndsWith (".fsproj", System.StringComparison.OrdinalIgnoreCase) then
        Path.GetFullPath lastArg
    else
        Path.Combine (__SOURCE_DIRECTORY__, "../../sample-project/App.fsproj")
        |> Path.GetFullPath

let cliArgs : CliArgs =
    {
        ProjectFile = fsproj
        RootDir = Path.GetDirectoryName fsproj
        OutDir = None
        IsWatch = false
        Precompile = false
        PrecompiledLib = None
        PrintAst = false
        FableLibraryPath = Some (Path.Combine (__SOURCE_DIRECTORY__, "../../node_modules/@fable-org/fable-library-js"))
        Configuration = "Debug"
        NoRestore = false
        NoCache = true
        NoGitignore = true
        NoParallelTypeCheck = false
        SourceMaps = false
        SourceMapsRoot = None
        Exclude = []
        Replace = Map.empty
        RunProcess = None
        CompilerOptions =
            {
                TypedArrays = true
                ClampByteArrays = false
                Language = Fable.Language.JavaScript
                Define = [ "FABLE_COMPILER" ; "FABLE_COMPILER_4" ; "FABLE_COMPILER_JAVASCRIPT" ]
                DebugMode = true
                OptimizeFSharpAst = false
                Verbosity = Fable.Verbosity.Verbose
                FileExtension = ".fs"
                TriggeredByDependency = false
                NoReflection = false
            }
        Verbosity = Fable.Verbosity.Verbose
    }

let options : CrackerOptions = CrackerOptions (cliArgs, true)

let logger =
    { new ILogger with
        member x.Log<'TState>
            (
                logLevel : LogLevel,
                _eventId : EventId,
                state : 'TState,
                ex : exn,
                formatter : System.Func<'TState, exn, string>
            )
            : unit
            =
            let level = string logLevel
            printfn $"%s{level}: %s{formatter.Invoke (state, ex)}"

        member x.BeginScope<'TState> (_state : 'TState) : System.IDisposable = null
        member x.IsEnabled (_logLevel : LogLevel) : bool = true
    }

// Fable's own resolver, no caching involved.
let fableResolver : ProjectCrackerResolver = MSBuildCrackerResolver ()

// The resolver the daemon uses: Fable's resolver wrapped with the design time build cache.
let cachedResolver : ProjectCrackerResolver = CachedMSBuildCrackerResolver logger

#time "on"

let result = cachedResolver.GetProjectOptionsFromProjectFile (true, options, fsproj)

#time "off"

for option in result.ProjectOptions do
    printfn "%s" option
