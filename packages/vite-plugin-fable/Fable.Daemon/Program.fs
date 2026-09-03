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
                        // Fable's own cache, off because the daemon keeps its own (`Caching.fs`).
                        // Upstream this flag only decides whether `CacheInfo` is read and written,
                        // and whether `fable_modules` is deleted wholesale, and that delete is
                        // guarded by `evaluateOnly` below. Nothing else reads it, so it costs no
                        // correctness to turn Fable's caching off here.
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

                // `evaluateOnly` is what stops `CrackerOptions` deleting the whole
                // `fable_modules` directory on construction, which it does when `NoCache` is set.
                // Named rather than a bare `true`, because that is a lot to hang on an unlabelled
                // boolean.
                cliArgs, CrackerOptions (cliArgs, evaluateOnly = true)

            // Which files the MSBuild evaluation depends on is decided by the evaluation, so the
            // keys from the previous crack describe the project as it was. Adding a
            // `Directory.Build.props` or an `<Import>` changes that list, and reusing the old key
            // would compare the new project against the old one's inputs and find them unchanged.
            model.Resolver.ForgetCacheKeys ()

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
        /// What Fable reported while translating. The F# diagnostics are not repeated here: the
        /// project was type-checked by the crack that came before, which already reported them.
        Diagnostics : Diagnostic array
        /// The files answered from the `fable_modules` cache rather than compiled just now.
        FromCache : Set<FullPath>
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
            Tag = "FSHARP"
        }
    )

/// What Fable itself reported while translating, dropping the F# diagnostics it reports alongside.
///
/// Since Fable 5.15 a compile answers with `Logs` rather than the type-check's diagnostics: the F#
/// half tagged `FSHARP` with its error number folded into the message, and everything Fable raised
/// tagged `FABLE`. The F# half is dropped here and taken from `FSharpDiagnostic` instead, which
/// still carries the error number as a field of its own.
///
/// This is what a file that type-checks but that Fable cannot translate reports. Before 5.15 those
/// logs were discarded inside `Fable.Compiler`, so such a file compiled to `return null` and the
/// build said nothing.
let private mapFableLogs (logs : Fable.Transforms.State.LogEntry array) : Diagnostic array =
    logs
    |> Array.filter (fun log -> log.Tag <> "FSHARP")
    |> Array.map (fun log ->
        {
            ErrorNumberText = ""
            Message = log.Message
            Range =
                match log.Range with
                | Some range ->
                    {
                        StartLine = range.start.line
                        StartColumn = range.start.column
                        EndLine = range.``end``.line
                        EndColumn = range.``end``.column
                    }
                | None ->
                    // What `Fable.Cli` prints for a log without a range, so the message still
                    // points at the file it is about.
                    {
                        StartLine = 1
                        StartColumn = 1
                        EndLine = 1
                        EndColumn = 1
                    }
            Severity =
                match log.Severity with
                | Severity.Error -> "Error"
                | Severity.Warning -> "Warning"
                | Severity.Info -> "Info"
            FileName = Option.defaultValue "" log.FileName
            Tag = log.Tag
        }
    )

/// What the debug endpoints report about the last crack. Built here because this is where the
/// cracker's own types are still in hand; `Debug` only ever sees plain data.
let private describeProject
    (resolver : CachedMSBuildCrackerResolver)
    (payload : ProjectChangedPayload)
    (result : TypeCheckedProjectData)
    : Debug.ProjectState
    =
    let fsproj = Path.GetFullPath payload.Project

    let reused, reason, detail =
        match resolver.CacheDecision fsproj with
        | Some (Ok ()) -> true, "", ""
        | Some (Error invalid) ->
            let name, detail = Caching.describeInvalidCacheReason invalid
            false, name, detail
        | None -> false, "unknown", "the resolver recorded no decision for this project"

    let cacheKey = resolver.TryGetCacheKey fsproj

    {
        Fsproj = fsproj
        Configuration = payload.Configuration
        FableLibrary = payload.FableLibrary
        Exclude = List.ofArray payload.Exclude
        NoReflection = payload.NoReflection
        SourceFiles = result.CrackerResponse.ProjectOptions.SourceFiles
        DependentFiles = result.DependentFiles
        TargetFramework = result.CrackerResponse.TargetFramework
        OutputType = Some (string result.CrackerResponse.OutputType)
        CompilerArgs = result.CrackerResponse.ProjectOptions.OtherOptions
        ProjectReferences = List.toArray result.CrackerResponse.References
        Diagnostics = mapDiagnostics result.TypeCheckProjectResult.ProjectCheckResults.Diagnostics
        CacheReused = reused
        CacheReason = reason
        CacheDetail = detail
        CacheFile =
            cacheKey
            |> Option.map (fun key -> key.CacheFile.FullName)
            |> Option.defaultValue ""
        FableModulesCacheFile =
            cacheKey
            |> Option.map (fun key -> key.FableModulesCacheFile.FullName)
            |> Option.defaultValue ""
    }

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

            return
                Ok
                    {
                        CompiledFSharpFiles = compiledFiles
                        Diagnostics = mapFableLogs initialCompileResponse.Logs
                        FromCache = cachedFableModuleFiles.Keys |> Set.ofSeq
                    }
        with ex ->
            logger.LogCritical ("tryCompileProject threw exception {ex}", ex)
            return Error ex.Message
    }

type CompiledFileData =
    {
        CompiledFiles : Map<string, string>
        /// The type-check of the project up to the last file compiled, plus whatever Fable
        /// reported while translating.
        Diagnostics : Diagnostic array
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
                        Diagnostics =
                            Array.append
                                (mapDiagnostics checkProjectResult.Diagnostics)
                                (mapFableLogs compiledFileResponse.Logs)
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

    do jsonMessageFormatter.JsonSerializerOptions <- Wire.serializerOptions ()

    let cts = new CancellationTokenSource ()

    do
        match logger with
        | :? Debug.InMemoryLogger as logger ->
            // A second dev server would collide on the default port, and Suave's bind failure is
            // swallowed by `Async.Start`. `VITE_PLUGIN_FABLE_DEBUG_PORT` is how you give the second
            // one a port of its own.
            let port =
                match Environment.GetEnvironmentVariable "VITE_PLUGIN_FABLE_DEBUG_PORT" with
                | null
                | "" -> Debug.defaultPort
                | raw ->
                    match UInt16.TryParse raw with
                    | true, port -> port
                    | _ -> Debug.defaultPort

            let server = Debug.startWebserver logger port cts.Token
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

                        if Debug.isEnabled () then
                            Debug.publishProject (describeProject model.Resolver payload result)

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
                        | Ok result ->
                            replyChannel.Reply (
                                FilesCompiledResult.Success (result.CompiledFSharpFiles, result.Diagnostics)
                            )

                            Debug.publishInitialCompile result.CompiledFSharpFiles result.FromCache result.Diagnostics

                        return Some model

                    | CompileFiles (fileNames, replyChannel) ->
                        let! result = tryCompileFiles logger model fileNames

                        match result with
                        | Error error -> replyChannel.Reply (FileChangedResult.Error error)
                        | Ok result ->
                            replyChannel.Reply (FileChangedResult.Success (result.CompiledFiles, result.Diagnostics))

                            Debug.publishFileCompile result.CompiledFiles result.Diagnostics (Array.ofList fileNames)

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
            let sw = Stopwatch.StartNew ()

            let! response =
                mailbox.PostAndAsyncReply (fun replyChannel -> Msg.ProjectChanged (p, replyChannel))

            sw.Stop ()

            Debug.recordRequest
                "fable/project-changed"
                p.Project
                sw.Elapsed.TotalMilliseconds
                (match response with
                 | ProjectChangedResult.Success _ -> "success"
                 | ProjectChangedResult.Error error -> $"error: %s{error}")

            logger.LogDebug ("exit \"fable/project-changed\" {response}", response)
            return response
        }

    [<JsonRpcMethod("fable/initial-compile", UseSingleObjectParameterDeserialization = true)>]
    member _.InitialCompile () : Task<FilesCompiledResult> =
        task {
            logger.LogDebug "enter \"fable/initial-compile\""
            let sw = Stopwatch.StartNew ()
            let! response = mailbox.PostAndAsyncReply Msg.CompileFullProject
            sw.Stop ()

            Debug.recordRequest
                "fable/initial-compile"
                ""
                sw.Elapsed.TotalMilliseconds
                (match response with
                 | FilesCompiledResult.Success (compiled, _) -> $"success: %i{compiled.Count} files"
                 | FilesCompiledResult.Error error -> $"error: %s{error}")

            let logResponse =
                match response with
                | FilesCompiledResult.Error e -> box e
                | FilesCompiledResult.Success (result, diagnostics) ->
                    let keys = result.Keys |> String.concat "\n" |> sprintf "\n%s"
                    box (keys, diagnostics)

            logger.LogDebug ("exit \"fable/initial-compile\" with {logResponse}", logResponse)
            return response
        }

    [<JsonRpcMethod("fable/compile", UseSingleObjectParameterDeserialization = true)>]
    member _.CompileFiles (p : CompileFilesPayload) : Task<FileChangedResult> =
        task {
            logger.LogDebug ("enter \"fable/compile\" with {p}", p)
            let sw = Stopwatch.StartNew ()

            let! response =
                mailbox.PostAndAsyncReply (fun replyChannel ->
                    Msg.CompileFiles (List.ofArray p.FileNames, replyChannel)
                )

            sw.Stop ()

            Debug.recordRequest
                "fable/compile"
                (String.concat ", " p.FileNames)
                sw.Elapsed.TotalMilliseconds
                (match response with
                 | FileChangedResult.Success (compiled, _) -> $"success: %i{compiled.Count} files"
                 | FileChangedResult.Error error -> $"error: %s{error}")

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
