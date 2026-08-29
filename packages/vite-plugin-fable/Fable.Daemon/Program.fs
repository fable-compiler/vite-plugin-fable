open System
open System.Collections.Concurrent
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

/// What every file in the project last contained, as Fable's `File` remembers it.
///
/// `File.ReadSource` hashes the source the first time it reads a file and hands back the content
/// lazily from then on, so a checker that only wants to know whether a file changed never pays for
/// the read. Building a fresh set of `File` values for every compile threw that away: every file in
/// the project, `fable_modules` included, was read and hashed again on every edit.
///
/// Keeping them means trusting the plugin to report every change, which it can: Vite watches the
/// whole root and the plugin adds the sources outside it to the watcher itself. A file that changed
/// without a report would be compiled from what it used to say, so anything less than certain about
/// which files changed has to `ForgetAll`.
///
/// Concurrent because the checker type-checks files in parallel, so the reader is called from
/// several threads at once. A `Dictionary` here corrupts itself under a project type-check.
type SourceFileCache() =
    let files = ConcurrentDictionary<FullPath, Fable.Compiler.File>()

    /// A reader over the project's sources, answering from what was read before where it can.
    member _.MakeSourceReader () : SourceReader =
        fun (path : FullPath) -> files.GetOrAdd(path, Fable.Compiler.File).ReadSource()

    /// Forget these files, because they changed on disk.
    member _.Forget (paths : FullPath seq) : unit =
        for path in paths do
            files.TryRemove path |> ignore

    /// Forget the lot. Which files the project even has is decided by a crack, so a new one makes
    /// everything read for the previous project unsafe to reuse.
    member _.ForgetAll () : unit = files.Clear ()

type Model =
    {
        Resolver : CachedMSBuildCrackerResolver
        Checker : InteractiveChecker
        CrackerInput : CrackerInput option
        CrackerResponse : CrackerResponse
        SourceFiles : SourceFileCache
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

            // A crack decides which files the project has, so nothing read for the previous one
            // can be assumed to still describe this project.
            model.SourceFiles.ForgetAll ()
            let sourceReader = model.SourceFiles.MakeSourceReader ()

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

        let! nextFiles =
            checker.GetDependentFiles (head, projectOptions.SourceFiles, sourceReader)

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

            // The files the plugin saw change, and so the only ones whose contents can differ from
            // what the last compile read.
            model.SourceFiles.Forget fileNames
            let sourceReader = model.SourceFiles.MakeSourceReader ()

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

/// The daemon's last resort. stdout carries the JSON-RPC framing so nothing else may be written
/// there, and `logger` is a `NullLogger` unless VITE_PLUGIN_FABLE_DEBUG is set, which leaves stderr
/// as the only channel that reaches a user. `startDaemon` forwards it to the Vite logger.
let private logCritical (logger : ILogger) (message : string) : unit =
    logger.LogCritical ("{message}", message)
    eprintfn $"[Fable.Daemon] {message}"

/// Names a message for a log line. Printing the message itself would include the reply channel.
let private describe (msg : Msg) : string =
    match msg with
    | Msg.ProjectChanged (payload, _) -> $"fable/project-changed for {payload.Project}"
    | Msg.CompileFullProject _ -> "fable/initial-compile"
    | Msg.CompileFiles (fileNames, _) -> $"""fable/compile for {String.concat ", " fileNames}"""
    | Msg.Disconnect -> "disconnect"

/// Answers whoever is waiting on `msg`, so a failure surfaces as a failed request rather than one
/// that never returns.
let private replyWithError (logger : ILogger) (msg : Msg) (error : string) : unit =
    try
        match msg with
        | Msg.ProjectChanged (_, replyChannel) -> replyChannel.Reply (ProjectChangedResult.Error error)
        | Msg.CompileFullProject replyChannel -> replyChannel.Reply (FilesCompiledResult.Error error)
        | Msg.CompileFiles (_, replyChannel) -> replyChannel.Reply (FileChangedResult.Error error)
        | Msg.Disconnect -> ()
    with replyFailure ->
        // Replying twice throws, so the request was already answered and the caller is not stuck.
        logCritical logger $"Could not report the failure of {describe msg}: {replyFailure}"

type FableServer(sender : Stream, reader : Stream, logger : ILogger) as this =
    let jsonMessageFormatter = new SystemTextJsonFormatter ()

    do
        jsonMessageFormatter.JsonSerializerOptions <-
            let options =
                JsonSerializerOptions (PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

            let jsonFSharpOptions =
                JsonFSharpOptions.Default().WithUnionTagName("case").WithUnionFieldsName("fields")

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
            /// Serves one message and returns the model to carry on with, or `None` to stop.
            let handle (model : Model) (msg : Msg) : Async<Model option> =
                async {
                    match msg with
                    | ProjectChanged (payload, replyChannel) ->
                        let! result = tryTypeCheckProject logger model payload

                        match result with
                        | Error error ->
                            replyChannel.Reply (ProjectChangedResult.Error error)
                            return Some model
                        | Ok result ->

                        replyChannel.Reply (
                            ProjectChangedResult.Success (
                                result.CrackerResponse.ProjectOptions.SourceFiles,
                                mapDiagnostics result.TypeCheckProjectResult.ProjectCheckResults.Diagnostics,
                                result.DependentFiles
                            )
                        )

                        return
                            Some
                                { model with
                                    CrackerInput = Some result.CrackerInput
                                    Checker = result.Checker
                                    CrackerResponse = result.CrackerResponse
                                    TypeCheckProjectResult = result.TypeCheckProjectResult
                                }

                    | CompileFullProject replyChannel ->
                        let! result = tryCompileProject logger model

                        match result with
                        | Error error -> replyChannel.Reply (FilesCompiledResult.Error error)
                        | Ok result -> replyChannel.Reply (FilesCompiledResult.Success result.CompiledFSharpFiles)

                        return Some model

                    | CompileFiles (fileNames, replyChannel) ->
                        let! result = tryCompileFiles logger model fileNames

                        match result with
                        | Error error -> replyChannel.Reply (FileChangedResult.Error error)
                        | Ok result ->
                            replyChannel.Reply (
                                FileChangedResult.Success (result.CompiledFiles, mapDiagnostics result.Diagnostics)
                            )

                        return Some model

                    | Disconnect -> return None
                }

            let rec loop (model : Model) =
                async {
                    let! msg = inbox.Receive ()

                    // Anything escaping `handle` used to kill the agent, and the request that
                    // provoked it was never answered. `PostAndAsyncReply` has no timeout, so the
                    // plugin waited on `buildStart` forever with nothing on screen, and every later
                    // request queued behind a loop that was gone.
                    let! next =
                        async {
                            try
                                return! handle model msg
                            with ex ->
                                logCritical logger $"Serving {describe msg} failed: {ex}"
                                replyWithError logger msg ex.Message
                                return Some model
                        }

                    match next with
                    | None -> return ()
                    // Recursing outside the `try` above, so a long-lived agent does not stack one
                    // exception handler per message it has served.
                    | Some model -> return! loop model
                }

            loop
                {
                    Resolver = CachedMSBuildCrackerResolver logger
                    Checker = Unchecked.defaultof<InteractiveChecker>
                    CrackerResponse = Unchecked.defaultof<CrackerResponse>
                    SourceFiles = SourceFileCache ()
                    PathResolver =
                        { new PathResolver with
                            member _.TryPrecompiledOutPath (_sourceDir, _relativePath) = None
                            member _.GetOrAddDeduplicateTargetDir (importDir, addTargetDir) = importDir
                        }
                    TypeCheckProjectResult = Unchecked.defaultof<TypeCheckProjectResult>
                    CrackerInput = None
                }
        )

    // The loop answers and survives its own failures, so reaching here means the agent itself is
    // gone. It cannot be restarted — the model went with it — and every later request would block
    // forever on `PostAndAsyncReply`, so stop the process instead. The plugin already turns an
    // unexpected daemon exit into an error the user sees.
    let subscription =
        mailbox.Error.Subscribe (fun ex ->
            logCritical logger $"The message loop stopped and the daemon cannot serve anything: {ex}"
            exit 1
        )

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

            let! response =
                mailbox.PostAndAsyncReply (fun replyChannel -> Msg.ProjectChanged (p, replyChannel))

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

AppDomain.CurrentDomain.ProcessExit.Add (fun _ -> (daemon :> IDisposable).Dispose())
daemon.WaitForClose.GetAwaiter().GetResult()
exit 0
