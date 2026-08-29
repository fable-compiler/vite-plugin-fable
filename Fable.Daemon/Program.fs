open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open StreamJsonRpc
open Fable
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.SourceCodeServices
open FSharp.Compiler.Diagnostics
open Fable.Compiler.ProjectCracker
open Fable.Compiler.Util
open Fable.Compiler
open Fable.Daemon

type Msg =
    | ProjectChanged of payload : ProjectChangedPayload * AsyncReplyChannel<ProjectChangedResult>
    | CompileFullProject of AsyncReplyChannel<FilesCompiledResult>
    | CompileFiles of fileNames : string list * AsyncReplyChannel<FileChangedResult>
    | Disconnect

/// Input for every getFullProjectOpts
/// Should be reused for subsequent type checks.
type CrackerInput =
    {
        CliArgs : CliArgs
        /// Reuse the cracker options in future design time builds
        CrackerOptions : CrackerOptions
    }

type Model =
    {
        Resolver : CachedMSBuildCrackerResolver
        Checker : InteractiveChecker
        CrackerInput : CrackerInput option
        CrackerResponse : CrackerResponse
        SourceReader : SourceReader
        PathResolver : PathResolver
        TypeCheckProjectResult : TypeCheckProjectResult
    }

let timeAsync f =
    async {
        let sw = Stopwatch.StartNew ()
        let! result = f
        sw.Stop ()
        return result, sw.Elapsed
    }

type TypeCheckedProjectData =
    {
        TypeCheckProjectResult : TypeCheckProjectResult
        CrackerInput : CrackerInput
        Checker : InteractiveChecker
        CrackerResponse : CrackerResponse
        SourceReader : SourceReader
        /// An array of files that influence the design time build
        /// If any of these change, the plugin should respond accordingly.
        DependentFiles : FullPath array
    }

let tryTypeCheckProject
    (logger : ILogger)
    (model : Model)
    (payload : ProjectChangedPayload)
    : Async<Result<TypeCheckedProjectData, string>>
    =
    async {
        try
            /// Project file will be in the Vite normalized format
            let projectFile = Path.GetFullPath payload.Project
            logger.LogDebug ("start tryTypeCheckProject for {projectFile}", projectFile)

            let cliArgs, crackerOptions =
                match model.CrackerInput with
                | Some {
                           CliArgs = cliArgs
                           CrackerOptions = crackerOptions
                       } -> cliArgs, crackerOptions
                | None ->

                let cliArgs : CliArgs =
                    {
                        ProjectFile = projectFile
                        RootDir = Path.GetDirectoryName payload.Project
                        OutDir = None
                        IsWatch = false
                        Precompile = false
                        PrecompiledLib = None
                        PrintAst = false
                        FableLibraryPath = Some payload.FableLibrary
                        Configuration = payload.Configuration
                        // Fable's MSBuildCrackerResolver only adds `/restore` to the design time build when this is false.
                        NoRestore = false
                        NoCache = true
                        NoGitignore = true
                        NoParallelTypeCheck = false
                        SourceMaps = false
                        SourceMapsRoot = None
                        Exclude = List.ofArray payload.Exclude
                        Replace = Map.empty
                        CompilerOptions =
                            {
                                TypedArrays = false
                                ClampByteArrays = false
                                Language = Language.JavaScript
                                Define = [ "FABLE_COMPILER" ; "FABLE_COMPILER_4" ; "FABLE_COMPILER_JAVASCRIPT" ]
                                DebugMode = false
                                OptimizeFSharpAst = false
                                Verbosity = Verbosity.Verbose
                                // We keep using `.fs` for the compiled FSharp file, even though the contents will be JavaScript.
                                FileExtension = ".fs"
                                TriggeredByDependency = false
                                NoReflection = payload.NoReflection
                            }
                        RunProcess = None
                        Verbosity = Verbosity.Verbose
                    }

                cliArgs, CrackerOptions (cliArgs, true)

            let crackerResponse = getFullProjectOpts model.Resolver crackerOptions

            logger.LogDebug ("CrackerResponse: {crackerResponse}", crackerResponse)
            let checker = InteractiveChecker.Create crackerResponse.ProjectOptions

            let sourceReader =
                Fable.Compiler.File.MakeSourceReader (
                    Array.map Fable.Compiler.File crackerResponse.ProjectOptions.SourceFiles
                )
                |> snd

            let! typeCheckResult, typeCheckTime =
                timeAsync (CodeServices.typeCheckProject sourceReader checker cliArgs crackerResponse)

            logger.LogDebug ("Typechecking {projectFile} took {elapsed}", projectFile, typeCheckTime)

            let dependentFiles =
                model.Resolver.MSBuildProjectFiles projectFile
                |> List.map (fun fi -> fi.FullName)
                |> List.toArray

            return
                Ok
                    {
                        TypeCheckProjectResult = typeCheckResult
                        CrackerInput =
                            Option.defaultValue
                                {
                                    CliArgs = cliArgs
                                    CrackerOptions = crackerOptions
                                }
                                model.CrackerInput
                        Checker = checker
                        CrackerResponse = crackerResponse
                        SourceReader = sourceReader
                        DependentFiles = dependentFiles
                    }
        with ex ->
            logger.LogCritical ("tryTypeCheckProject threw exception {ex}", ex)
            return Error ex.Message
    }

type CompiledProjectData =
    {
        CompiledFSharpFiles : Map<string, string>
    }

let private mapRange (m : FSharp.Compiler.Text.range) =
    {
        StartLine = m.StartLine
        StartColumn = m.StartColumn
        EndLine = m.EndLine
        EndColumn = m.EndColumn
    }

let private mapDiagnostics (ds : FSharpDiagnostic array) =
    ds
    |> Array.map (fun d ->
        {
            ErrorNumberText = d.ErrorNumberText
            Message = d.Message
            Range = mapRange d.Range
            Severity = string d.Severity
            FileName = d.FileName
        }
    )

let tryCompileProject (logger : ILogger) (model : Model) : Async<Result<CompiledProjectData, string>> =
    async {
        try
            let cachedFableModuleFiles =
                model.Resolver.TryGetCachedFableModuleFiles model.CrackerResponse.ProjectOptions.ProjectFileName

            let files =
                let cachedFiles = cachedFableModuleFiles.Keys |> Set.ofSeq

                model.CrackerResponse.ProjectOptions.SourceFiles
                |> Array.filter (fun sf ->
                    not (sf.EndsWith (".fsi", StringComparison.Ordinal))
                    && not (cachedFiles.Contains sf)
                )

            match model.CrackerInput with
            | None ->
                logger.LogCritical "tryCompileProject is entered without CrackerInput"
                return raise (exn "tryCompileProject is entered without CrackerInput")
            | Some { CliArgs = cliArgs } ->

            let! initialCompileResponse =
                CodeServices.compileMultipleFilesToJavaScript
                    model.PathResolver
                    cliArgs
                    model.CrackerResponse
                    model.TypeCheckProjectResult
                    files

            if cachedFableModuleFiles.IsEmpty then
                let fableModuleFiles =
                    initialCompileResponse.CompiledFiles
                    |> Map.filter (fun key _value -> key.Contains "fable_modules")

                model.Resolver.WriteCachedFableModuleFiles
                    model.CrackerResponse.ProjectOptions.ProjectFileName
                    fableModuleFiles

            let compiledFiles =
                (initialCompileResponse.CompiledFiles, cachedFableModuleFiles)
                ||> Map.fold (fun state key value -> Map.add key value state)

            return Ok { CompiledFSharpFiles = compiledFiles }
        with ex ->
            logger.LogCritical ("tryCompileProject threw exception {ex}", ex)
            return Error ex.Message
    }

type CompiledFileData =
    {
        CompiledFiles : Map<string, string>
        Diagnostics : FSharpDiagnostic array
    }

/// Find all the dependent files as efficient as possible.
let rec getDependentFiles
    (sourceReader : SourceReader)
    (projectOptions : FSharpProjectOptions)
    (checker : InteractiveChecker)
    (inputFiles : string list)
    (result : Set<string>)
    : Async<Set<string>>
    =
    async {
        match inputFiles with
        | [] ->
            // Filter out the signature files at the end.
            return
                result
                |> Set.filter (fun f -> not (f.EndsWith (".fsi", StringComparison.Ordinal)))
        | head :: tail ->

        // If the file is already part of the collection, it can safely be skipped.
        if result.Contains head then
            return! getDependentFiles sourceReader projectOptions checker tail result
        else

        let! nextFiles = checker.GetDependentFiles (head, projectOptions.SourceFiles, sourceReader)
        let nextResult = (result, nextFiles) ||> Array.fold (fun acc f -> Set.add f acc)

        return! getDependentFiles sourceReader projectOptions checker tail nextResult
    }

let tryCompileFiles
    (logger : ILogger)
    (model : Model)
    (fileNames : string list)
    : Async<Result<CompiledFileData, string>>
    =
    async {
        try
            let fileNames = List.map Path.normalizePath fileNames
            logger.LogDebug ("tryCompileFile {fileNames}", fileNames)

            match model.CrackerInput with
            | None ->
                logger.LogCritical "tryCompileFile is entered without CrackerInput"
                return raise (exn "tryCompileFile is entered without CrackerInput")
            | Some { CliArgs = cliArgs } ->

            // Choose the signature file in the pair if it exists.
            let mapLeadingFile (file : string) : string =
                if file.EndsWith (".fsi", StringComparison.Ordinal) then
                    file
                else
                    model.CrackerResponse.ProjectOptions.SourceFiles
                    |> Array.tryFind (fun f -> f = String.Concat (file, "i"))
                    |> Option.defaultValue file

            let sourceReader =
                Fable.Compiler.File.MakeSourceReader (
                    Array.map Fable.Compiler.File model.CrackerResponse.ProjectOptions.SourceFiles
                )
                |> snd

            let! filesToCompile =
                let input = List.map mapLeadingFile fileNames
                getDependentFiles sourceReader model.CrackerResponse.ProjectOptions model.Checker input Set.empty

            logger.LogDebug ("About to compile {allFiles}", filesToCompile)

            // Type-check the project up until the last file
            let lastFile =
                model.CrackerResponse.ProjectOptions.SourceFiles
                |> Array.tryFindBack filesToCompile.Contains
                |> Option.defaultValue (Array.last model.CrackerResponse.ProjectOptions.SourceFiles)

            let! checkProjectResult =
                model.Checker.ParseAndCheckProject (
                    cliArgs.ProjectFile,
                    model.CrackerResponse.ProjectOptions.SourceFiles,
                    sourceReader,
                    lastFile = lastFile
                )

            let! compiledFileResponse =
                Fable.Compiler.CodeServices.compileMultipleFilesToJavaScript
                    model.PathResolver
                    cliArgs
                    model.CrackerResponse
                    { model.TypeCheckProjectResult with
                        ProjectCheckResults = checkProjectResult
                    }
                    filesToCompile

            return
                Ok
                    {
                        CompiledFiles = compiledFileResponse.CompiledFiles
                        Diagnostics = compiledFileResponse.Diagnostics
                    }
        with ex ->
            logger.LogCritical ("tryCompileFile threw exception {ex}", ex)
            return Error ex.Message
    }

type FableServer(sender : Stream, reader : Stream, logger : ILogger) as this =
    let jsonMessageFormatter = new SystemTextJsonFormatter ()

    do
        jsonMessageFormatter.JsonSerializerOptions <-
            let options =
                JsonSerializerOptions (PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

            let jsonFSharpOptions =
                JsonFSharpOptions.Default().WithUnionTagName("case").WithUnionFieldsName ("fields")

            options.Converters.Add (JsonUnionConverter jsonFSharpOptions)
            options

    let cts = new CancellationTokenSource ()

    do
        match logger with
        | :? Debug.InMemoryLogger as logger ->
            let server = Debug.startWebserver logger cts.Token
            Async.Start (server, cts.Token)
        | _ -> ()

    let handler =
        new HeaderDelimitedMessageHandler (sender, reader, jsonMessageFormatter)

    let rpc : JsonRpc = new JsonRpc (handler, this)
    do rpc.StartListening ()

    let mailbox =
        MailboxProcessor.Start (fun inbox ->
            let rec loop (model : Model) =
                async {
                    let! msg = inbox.Receive ()

                    match msg with
                    | ProjectChanged (payload, replyChannel) ->
                        let! result = tryTypeCheckProject logger model payload

                        match result with
                        | Error error ->
                            replyChannel.Reply (ProjectChangedResult.Error error)
                            return! loop model
                        | Ok result ->

                        replyChannel.Reply (
                            ProjectChangedResult.Success (
                                result.CrackerResponse.ProjectOptions.SourceFiles,
                                mapDiagnostics result.TypeCheckProjectResult.ProjectCheckResults.Diagnostics,
                                result.DependentFiles
                            )
                        )

                        return!
                            loop
                                { model with
                                    CrackerInput = Some result.CrackerInput
                                    Checker = result.Checker
                                    CrackerResponse = result.CrackerResponse
                                    SourceReader = result.SourceReader
                                    TypeCheckProjectResult = result.TypeCheckProjectResult
                                }

                    | CompileFullProject replyChannel ->
                        let! result = tryCompileProject logger model

                        match result with
                        | Error error ->
                            replyChannel.Reply (FilesCompiledResult.Error error)
                            return! loop model
                        | Ok result ->
                            replyChannel.Reply (FilesCompiledResult.Success result.CompiledFSharpFiles)

                            return! loop model

                    | CompileFiles (fileNames, replyChannel) ->
                        let! result = tryCompileFiles logger model fileNames

                        match result with
                        | Error error -> replyChannel.Reply (FileChangedResult.Error error)
                        | Ok result ->
                            replyChannel.Reply (
                                FileChangedResult.Success (result.CompiledFiles, mapDiagnostics result.Diagnostics)
                            )

                        return! loop model
                    | Disconnect -> return ()
                }

            loop
                {
                    Resolver = CachedMSBuildCrackerResolver logger
                    Checker = Unchecked.defaultof<InteractiveChecker>
                    CrackerResponse = Unchecked.defaultof<CrackerResponse>
                    SourceReader = Unchecked.defaultof<SourceReader>
                    PathResolver =
                        { new PathResolver with
                            member _.TryPrecompiledOutPath (_sourceDir, _relativePath) = None
                            member _.GetOrAddDeduplicateTargetDir (importDir, addTargetDir) = importDir
                        }
                    TypeCheckProjectResult = Unchecked.defaultof<TypeCheckProjectResult>
                    CrackerInput = None
                }
        )

    // log or something.
    let subscription = mailbox.Error.Subscribe (fun evt -> ())

    interface IDisposable with
        member _.Dispose () =
            if not (isNull subscription) then
                subscription.Dispose ()

            if not cts.IsCancellationRequested then
                cts.Cancel ()

            ()

    /// returns a hot task that resolves when the stream has terminated
    member this.WaitForClose = rpc.Completion

    [<JsonRpcMethod("fable/project-changed", UseSingleObjectParameterDeserialization = true)>]
    member _.ProjectChanged (p : ProjectChangedPayload) : Task<ProjectChangedResult> =
        task {
            logger.LogDebug ("enter \"fable/project-changed\" {p}", p)
            let! response = mailbox.PostAndAsyncReply (fun replyChannel -> Msg.ProjectChanged (p, replyChannel))
            logger.LogDebug ("exit \"fable/project-changed\" {response}", response)
            return response
        }

    [<JsonRpcMethod("fable/initial-compile", UseSingleObjectParameterDeserialization = true)>]
    member _.InitialCompile () : Task<FilesCompiledResult> =
        task {
            logger.LogDebug "enter \"fable/initial-compile\""
            let! response = mailbox.PostAndAsyncReply Msg.CompileFullProject

            let logResponse =
                match response with
                | FilesCompiledResult.Error e -> box e
                | FilesCompiledResult.Success result -> result.Keys |> String.concat "\n" |> sprintf "\n%s" |> box

            logger.LogDebug ("exit \"fable/initial-compile\" with {logResponse}", logResponse)
            return response
        }

    [<JsonRpcMethod("fable/compile", UseSingleObjectParameterDeserialization = true)>]
    member _.CompileFiles (p : CompileFilesPayload) : Task<FileChangedResult> =
        task {
            logger.LogDebug ("enter \"fable/compile\" with {p}", p)

            let! response =
                mailbox.PostAndAsyncReply (fun replyChannel ->
                    Msg.CompileFiles (List.ofArray p.FileNames, replyChannel)
                )

            let logResponse =
                match response with
                | FileChangedResult.Error e -> box e
                | FileChangedResult.Success (result, diagnostics) ->
                    let keys = result.Keys |> String.concat "\n" |> sprintf "\n%s"
                    box (keys, diagnostics)

            logger.LogDebug ("exit \"fable/compile\" with {p}", logResponse)
            return response
        }

let input = Console.OpenStandardInput ()
let output = Console.OpenStandardOutput ()

let logger : ILogger =
    let envVar = Environment.GetEnvironmentVariable "VITE_PLUGIN_FABLE_DEBUG"

    if not (String.IsNullOrWhiteSpace envVar) && not (envVar = "0") then
        Debug.InMemoryLogger ()
    else
        NullLogger.Instance

// Set Fable logger
Log.setLogger Verbosity.Verbose logger

let daemon =
    new FableServer (Console.OpenStandardOutput (), Console.OpenStandardInput (), logger)

AppDomain.CurrentDomain.ProcessExit.Add (fun _ -> (daemon :> IDisposable).Dispose ())
daemon.WaitForClose.GetAwaiter().GetResult ()
exit 0
