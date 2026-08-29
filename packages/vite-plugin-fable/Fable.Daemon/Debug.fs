module Fable.Daemon.Debug

open System
open System.Collections.Generic
open System.Text
open System.Text.Json
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Threading
open Suave
open Suave.Filters
open Suave.Operators
open Suave.Successful
open Suave.Logging
open Suave.Sockets
open Suave.Sockets.Control
open Suave.WebSocket
open Microsoft.Extensions.Logging

let defaultPort = 9014us

/// We can't log anything to the stdout!
let zeroSuaveLogger : Logger =
    { new Logger with
        member x.log level _ = ()
        member x.logWithAck _ _ = async.Zero ()
        member x.name = [| "vite-plugin-fable" |]
    }

/// Next to the assembly rather than `__SOURCE_DIRECTORY__`, which bakes in the path the daemon was
/// compiled in. That was the same folder while the daemon was built on the machine that ran it, and
/// is a stranger's home directory now that it ships prebuilt.
let homeFolder = Path.Combine (AppContext.BaseDirectory, "debug")

type LogEntry =
    {
        Level : string
        Exception : exn
        Message : string
        TimeStamp : DateTime
    }

module HTML =
    open Fable.React

    let mapLogEntriesToListItems (logEntries : LogEntry seq) =
        logEntries
        |> Seq.map (fun entry ->
            li
                []
                [
                    strong [] [ str entry.Level ]
                    time [] [ str (entry.TimeStamp.ToString "HH:mm:ss.fff") ]
                    pre [] [ str entry.Message ]
                ]
        )
        |> fragment []
        |> Fable.ReactServer.renderToString

/// Dictionary of client and how many messages they received
let connectedClients = ConcurrentDictionary<WebSocket, int>()

type InMemoryLogger() =
    let entries = Queue<LogEntry>()

    /// The queue is written by whichever thread logged and now read by the endpoints as well, so
    /// every touch of it is serialised. An unsynchronised `Queue` throws mid-enumeration when
    /// another thread enqueues, which would take out a request that only wanted to read the log.
    let sync = obj ()

    let broadCastNewMessages () =
        for KeyValue (client, currentCount) in connectedClients do
            let messages =
                entries
                |> Seq.skip currentCount
                |> HTML.mapLogEntriesToListItems
                |> Encoding.UTF8.GetBytes
                |> ByteSegment

            client.send Text messages true //
            |> Async.Ignore
            |> Async.RunSynchronously

            connectedClients.[client] <- entries.Count

    member _.Entries : LogEntry array = lock sync (fun () -> entries.ToArray ())
    member _.Count : int = lock sync (fun () -> entries.Count)

    interface ILogger with
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
            lock
                sync
                (fun () ->
                    entries.Enqueue
                        {
                            Level = string logLevel
                            Exception = ex
                            Message = formatter.Invoke (state, ex)
                            TimeStamp = DateTime.Now
                        }

                    broadCastNewMessages ()
                )

        member x.BeginScope<'TState> (_state : 'TState) : IDisposable = null
        member x.IsEnabled (_logLevel : LogLevel) : bool = true

// ---------------------------------------------------------------------------------------------
// What the daemon last finished doing.
//
// `Model` lives inside the message loop, and a cold crack holds it for seconds. Asking the loop
// would mean every request queueing behind whatever it is compiling, so instead the loop publishes
// an immutable snapshot after each message it serves and the endpoints read that. What they serve
// is therefore the last *completed* state, which is the only thing that is true anyway.
// ---------------------------------------------------------------------------------------------

type ProjectState =
    {
        Fsproj : FullPath
        Configuration : string
        FableLibrary : FullPath
        Exclude : string list
        NoReflection : bool
        SourceFiles : FullPath array
        DependentFiles : FullPath array
        TargetFramework : string option
        OutputType : string option
        CompilerArgs : string array
        ProjectReferences : string array
        Diagnostics : Diagnostic array
        CacheReused : bool
        CacheReason : string
        CacheDetail : string
        CacheFile : FullPath
        FableModulesCacheFile : FullPath
    }

type CompileState =
    {
        CompiledFiles : Map<FullPath, JavaScript>
        FromCache : Set<FullPath>
        Diagnostics : Diagnostic array
        LastRequested : FullPath array
        LastCompiledAt : DateTime
    }

type Snapshot =
    {
        /// Incremented for every message the loop serves. A caller can tell whether what it is
        /// reading already includes the edit it made, which no timestamp reliably answers.
        Revision : int
        UpdatedAt : DateTime
        Project : ProjectState option
        Compile : CompileState option
    }

type RequestEntry =
    {
        Method : string
        Detail : string
        StartedAt : DateTime
        DurationMs : float
        Outcome : string
    }

let emptySnapshot =
    {
        Revision = 0
        UpdatedAt = DateTime.Now
        Project = None
        Compile = None
    }

let snapshot = ref emptySnapshot
let requests = ConcurrentQueue<RequestEntry>()

[<Literal>]
let maxRequests = 100

let processStartedAt = DateTime.Now
let mutable serverEnabled = false

let isEnabled () : bool = serverEnabled

/// Only the message loop publishes, so read-modify-write needs no interlocking. Readers only ever
/// see a whole snapshot, because the reference is swapped rather than mutated in place.
let advance (f : Snapshot -> Snapshot) : unit =
    if serverEnabled then
        let current = snapshot.Value

        snapshot.Value <-
            { f current with
                Revision = current.Revision + 1
                UpdatedAt = DateTime.Now
            }

let publishProject (project : ProjectState) : unit =
    advance (fun s ->
        { s with
            Project = Some project
            Compile = None
        }
    )

let publishInitialCompile (compiled : Map<FullPath, JavaScript>) (fromCache : Set<FullPath>) : unit =
    advance (fun s ->
        { s with
            Compile =
                Some
                    {
                        CompiledFiles = compiled
                        FromCache = fromCache
                        Diagnostics = Array.empty
                        LastRequested = Array.empty
                        LastCompiledAt = DateTime.Now
                    }
        }
    )

let publishFileCompile
    (compiled : Map<FullPath, JavaScript>)
    (diagnostics : Diagnostic array)
    (requested : FullPath array)
    : unit
    =
    advance (fun s ->
        let previous = s.Compile

        let merged =
            match previous with
            | None -> compiled
            | Some c ->
                (c.CompiledFiles, compiled)
                ||> Map.fold (fun acc key value -> Map.add key value acc)

        { s with
            Compile =
                Some
                    {
                        CompiledFiles = merged
                        FromCache =
                            match previous with
                            | None -> Set.empty
                            | Some c -> c.FromCache
                        Diagnostics = diagnostics
                        LastRequested = requested
                        LastCompiledAt = DateTime.Now
                    }
        }
    )

let recordRequest (method : string) (detail : string) (durationMs : float) (outcome : string) : unit =
    if serverEnabled then
        requests.Enqueue
            {
                Method = method
                Detail = detail
                StartedAt = DateTime.Now
                DurationMs = durationMs
                Outcome = outcome
            }

        while requests.Count > maxRequests do
            requests.TryDequeue () |> ignore

// ---------------------------------------------------------------------------------------------
// The JSON endpoints.
// ---------------------------------------------------------------------------------------------

let serializerOptions =
    JsonSerializerOptions (PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true)

let serialize (value : 'T) : string =
    JsonSerializer.Serialize<'T>(value, serializerOptions)

let jsonMime = Writers.setMimeType "application/json; charset=utf-8"

let ok (value : 'T) : WebPart = OK (serialize value) >=> jsonMime

let failWith (respond : string -> WebPart) (message : string) : WebPart =
    respond (serialize {| error = message |}) >=> jsonMime

/// Nothing has been cracked yet, so there is no project to answer about.
let noProject : WebPart =
    failWith RequestErrors.CONFLICT "No project has been cracked yet. Wait for the first compile."

let relativeTo (root : string) (file : string) : string =
    try
        Path.GetRelativePath (root, file)
    with _ ->
        file

let rootOf (project : ProjectState) : string =
    try
        Path.GetDirectoryName project.Fsproj
    with _ ->
        ""

let isError (d : Diagnostic) =
    d.Severity.ToLowerInvariant () = "error"

let isWarning (d : Diagnostic) =
    d.Severity.ToLowerInvariant () = "warning"

/// Both forms of every path: absolute is what the daemon holds, relative is what a caller greps for.
let describeFile (root : string) (index : int) (compile : CompileState option) (file : FullPath) =
    let javaScript = compile |> Option.bind (fun c -> Map.tryFind file c.CompiledFiles)

    let diagnosticsFor (ds : Diagnostic array) =
        ds
        |> Array.filter (fun d -> String.Equals (d.FileName, file, StringComparison.OrdinalIgnoreCase))

    {|
        path = file
        relativePath = relativeTo root file
        index = index
        compiled = Option.isSome javaScript
        javaScriptLength = javaScript |> Option.map String.length |> Option.defaultValue 0
        fromFableModulesCache =
            match compile with
            | None -> false
            | Some c -> c.FromCache.Contains file
        errorCount =
            match compile with
            | None -> 0
            | Some c -> c.Diagnostics |> diagnosticsFor |> Array.filter isError |> Array.length
    |}

let endpointIndex =
    {|
        service = "vite-plugin-fable daemon"
        readOnly = true
        note =
            "Every response carries the revision the daemon was at. Nothing here compiles, cracks or invalidates anything."
        endpoints =
            [|
                {|
                    path = "/api"
                    answers = "This index."
                |}
                {|
                    path = "/api/status"
                    answers = "Is the daemon alive, has it cracked anything, and how much of it compiled."
                |}
                {|
                    path = "/api/project"
                    answers =
                        "The crack result: source files in compile order, watched MSBuild inputs, target framework. Add ?include=args,references for the compiler arguments and assembly references."
                |}
                {|
                    path = "/api/files"
                    answers =
                        "Every source file with its compile-order index and emitted size. Add ?path=<file> for one file including its JavaScript, and ?source=false to leave the JavaScript out."
                |}
                {|
                    path = "/api/diagnostics"
                    answers =
                        "Current diagnostics, unfiltered, tagged with whether they came from the type-check or the last compile. Filter with ?severity=error and ?file=<file>."
                |}
                {|
                    path = "/api/cache"
                    answers = "Whether the design time build cache answered the last crack, and why not."
                |}
                {|
                    path = "/api/requests"
                    answers = "The last 100 JSON-RPC requests with their durations and outcomes."
                |}
                {|
                    path = "/api/logs"
                    answers = "The in-memory log as JSON. Filter with ?since=<index>, ?limit=<n> and ?level=<name>."
                |}
                {|
                    path = "/"
                    answers = "The human log viewer, which streams over /ws."
                |}
            |]
    |}

let statusPayload (logger : InMemoryLogger) (port : uint16) =
    let s = snapshot.Value

    let diagnostics =
        match s.Project, s.Compile with
        | None, None -> Array.empty
        | Some p, None -> p.Diagnostics
        | None, Some c -> c.Diagnostics
        | Some p, Some c -> Array.append p.Diagnostics c.Diagnostics

    {|
        revision = s.Revision
        updatedAt = s.UpdatedAt
        pid = Environment.ProcessId
        port = int port
        uptimeSeconds = Math.Round ((DateTime.Now - processStartedAt).TotalSeconds, 1)
        fableCompilerVersion = Caching.fableCompilerVersion
        projectLoaded = Option.isSome s.Project
        fsproj = s.Project |> Option.map (fun p -> p.Fsproj) |> Option.toObj
        configuration = s.Project |> Option.map (fun p -> p.Configuration) |> Option.toObj
        sourceFileCount =
            match s.Project with
            | None -> 0
            | Some p -> p.SourceFiles.Length
        compiledFileCount =
            match s.Compile with
            | None -> 0
            | Some c -> c.CompiledFiles.Count
        errorCount = diagnostics |> Array.filter isError |> Array.length
        warningCount = diagnostics |> Array.filter isWarning |> Array.length
        logCount = logger.Count
        requestCount = requests.Count
    |}

let projectPayload (includes : string) (project : ProjectState) =
    let root = rootOf project
    let wants (what : string) = includes.Contains what

    {|
        revision = snapshot.Value.Revision
        fsproj = project.Fsproj
        rootDir = root
        configuration = project.Configuration
        fableLibrary = project.FableLibrary
        exclude = project.Exclude
        noReflection = project.NoReflection
        targetFramework = Option.toObj project.TargetFramework
        outputType = Option.toObj project.OutputType
        sourceFiles =
            project.SourceFiles
            |> Array.mapi (fun index file ->
                {|
                    path = file
                    relativePath = relativeTo root file
                    index = index
                |}
            )
        dependentFiles =
            project.DependentFiles
            |> Array.map (fun file ->
                {|
                    path = file
                    relativePath = relativeTo root file
                |}
            )
        projectReferenceCount = project.ProjectReferences.Length
        compilerArgCount = project.CompilerArgs.Length
        compilerArgs = if wants "args" then project.CompilerArgs else Array.empty
        projectReferences =
            if wants "references" then
                project.ProjectReferences
            else
                Array.empty
    |}

let filesPayload (project : ProjectState) (compile : CompileState option) =
    let root = rootOf project

    {|
        revision = snapshot.Value.Revision
        count = project.SourceFiles.Length
        files =
            project.SourceFiles
            |> Array.mapi (fun index file -> describeFile root index compile file)
    |}

/// A caller can name a file however it has it: absolute, or relative to the project.
let resolveFile (project : ProjectState) (asked : string) : FullPath option =
    let candidate =
        if Path.IsPathRooted asked then
            Path.GetFullPath asked
        else
            Path.GetFullPath (Path.Combine (rootOf project, asked))

    project.SourceFiles
    |> Array.tryFind (fun file -> String.Equals (file, candidate, StringComparison.OrdinalIgnoreCase))

let filePayload (project : ProjectState) (compile : CompileState option) (withSource : bool) (file : FullPath) =
    let root = rootOf project
    let index = project.SourceFiles |> Array.findIndex (fun f -> f = file)
    let summary = describeFile root index compile file

    let diagnosticsFor (source : string) (ds : Diagnostic array) =
        ds
        |> Array.filter (fun d -> String.Equals (d.FileName, file, StringComparison.OrdinalIgnoreCase))
        |> Array.map (fun d -> {| source = source ; diagnostic = d |})

    {|
        revision = snapshot.Value.Revision
        file = summary
        javaScript =
            if not withSource then
                null
            else
                compile
                |> Option.bind (fun c -> Map.tryFind file c.CompiledFiles)
                |> Option.toObj
        diagnostics =
            Array.append
                (diagnosticsFor "type-check" project.Diagnostics)
                (match compile with
                 | None -> Array.empty
                 | Some c -> diagnosticsFor "compile" c.Diagnostics)
    |}

let diagnosticsPayload
    (severity : string option)
    (file : string option)
    (project : ProjectState)
    (compile : CompileState option)
    =
    let tagged =
        Array.append
            (project.Diagnostics
             |> Array.map (fun d ->
                 {|
                     source = "type-check"
                     diagnostic = d
                 |}
             ))
            (match compile with
             | None -> Array.empty
             | Some c ->
                 c.Diagnostics
                 |> Array.map (fun d ->
                     {|
                         source = "compile"
                         diagnostic = d
                     |}
                 ))

    let matchesSeverity (d : Diagnostic) =
        match severity with
        | None -> true
        | Some wanted -> String.Equals (d.Severity, wanted, StringComparison.OrdinalIgnoreCase)

    let matchesFile (d : Diagnostic) =
        match file with
        | None -> true
        | Some wanted -> d.FileName.Contains (wanted, StringComparison.OrdinalIgnoreCase)

    let filtered =
        tagged
        |> Array.filter (fun entry -> matchesSeverity entry.diagnostic && matchesFile entry.diagnostic)

    {|
        revision = snapshot.Value.Revision
        count = filtered.Length
        errorCount = filtered |> Array.filter (fun e -> isError e.diagnostic) |> Array.length
        warningCount = filtered |> Array.filter (fun e -> isWarning e.diagnostic) |> Array.length
        diagnostics = filtered
    |}

let cachePayload (project : ProjectState) =
    let root = rootOf project

    {|
        revision = snapshot.Value.Revision
        reused = project.CacheReused
        reason = project.CacheReason
        detail = project.CacheDetail
        cacheFile = project.CacheFile
        fableModulesCacheFile = project.FableModulesCacheFile
        dependentFiles =
            project.DependentFiles
            |> Array.map (fun file ->
                {|
                    path = file
                    relativePath = relativeTo root file
                    exists = File.Exists file
                |}
            )
    |}

let requestsPayload () =
    let entries = requests.ToArray ()

    {|
        revision = snapshot.Value.Revision
        count = entries.Length
        // Projected rather than serialised as-is: `RequestEntry` is not in the signature file, so
        // it is internal, and System.Text.Json writes an internal type's properties as `{}`.
        requests =
            entries
            |> Array.map (fun entry ->
                {|
                    method = entry.Method
                    detail = entry.Detail
                    startedAt = entry.StartedAt
                    durationMs = entry.DurationMs
                    outcome = entry.Outcome
                |}
            )
    |}

let logsPayload (since : int) (limit : int) (level : string option) (logger : InMemoryLogger) =
    let all = logger.Entries

    let selected =
        all
        |> Array.indexed
        |> Array.filter (fun (index, entry) ->
            index >= since
            && (
                match level with
                | None -> true
                | Some wanted -> String.Equals (entry.Level, wanted, StringComparison.OrdinalIgnoreCase)
            )
        )

    let page =
        if limit <= 0 then
            selected
        else
            Array.truncate limit selected

    {|
        revision = snapshot.Value.Revision
        total = all.Length
        // Where to resume from, so a caller polling the log does not re-read what it already has.
        nextSince = all.Length
        count = page.Length
        entries =
            page
            |> Array.map (fun (index, entry) ->
                {|
                    index = index
                    level = entry.Level
                    message = entry.Message
                    timestamp = entry.TimeStamp
                    error =
                        if isNull entry.Exception then
                            null
                        else
                            string entry.Exception
                |}
            )
    |}

let queryValue (request : HttpRequest) (name : string) : string option =
    match request.queryParam name with
    | Choice1Of2 value when not (String.IsNullOrWhiteSpace value) -> Some value
    | _ -> None

let queryInt (request : HttpRequest) (name : string) (fallback : int) : int =
    match queryValue request name with
    | None -> fallback
    | Some raw ->
        match Int32.TryParse raw with
        | true, value -> value
        | _ -> fallback

/// Every endpoint that needs a cracked project, with the 409 in one place.
let withProject (f : ProjectState -> CompileState option -> WebPart) : WebPart =
    fun ctx ->
        let s = snapshot.Value

        match s.Project with
        | None -> noProject ctx
        | Some project -> f project s.Compile ctx

let api (logger : InMemoryLogger) (port : uint16) : WebPart =
    choose
        [
            path "/api" >=> ok endpointIndex
            // Built per request rather than once: `ok (statusPayload ...)` here would serialise
            // the snapshot as it was when the server started and serve that forever.
            path "/api/status" >=> fun ctx -> ok (statusPayload logger port) ctx
            path "/api/project"
            >=> request (fun r ->
                withProject (fun project _ -> ok (projectPayload (defaultArg (queryValue r "include") "") project))
            )
            path "/api/files"
            >=> request (fun r ->
                withProject (fun project compile ->
                    match queryValue r "path" with
                    | None -> ok (filesPayload project compile)
                    | Some asked ->

                    match resolveFile project asked with
                    | None ->
                        failWith
                            RequestErrors.NOT_FOUND
                            $"%s{asked} is not a source file of %s{project.Fsproj}. GET /api/files lists them."
                    | Some file ->
                        let withSource = defaultArg (queryValue r "source") "true" <> "false"
                        ok (filePayload project compile withSource file)
                )
            )
            path "/api/diagnostics"
            >=> request (fun r ->
                withProject (fun project compile ->
                    ok (diagnosticsPayload (queryValue r "severity") (queryValue r "file") project compile)
                )
            )
            path "/api/cache" >=> withProject (fun project _ -> ok (cachePayload project))
            path "/api/requests" >=> fun ctx -> ok (requestsPayload ()) ctx
            path "/api/logs"
            >=> request (fun r ->
                ok (logsPayload (queryInt r "since" 0) (queryInt r "limit" 0) (queryValue r "level") logger)
            )
        ]

let ws (logger : InMemoryLogger) (webSocket : WebSocket) (context : HttpContext) =
    context.runtime.logger.info (Message.eventX $"New websocket connection")
    connectedClients.TryAdd (webSocket, logger.Count) |> ignore

    socket {
        let mutable loop = true

        while loop do
            let! msg = webSocket.read ()

            match msg with
            | Close, _, _ ->
                connectedClients.TryRemove webSocket |> ignore
                let emptyResponse = [||] |> ByteSegment
                do! webSocket.send Close emptyResponse true
                loop <- false

            | _ -> ()
    }

let webApp (logger : InMemoryLogger) (port : uint16) : WebPart =
    let allLogs ctx =
        let html = logger.Entries |> HTML.mapLogEntriesToListItems
        (OK html >=> Writers.setMimeType "text/html") ctx

    choose
        [
            path "/ws" >=> handShake (ws logger)
            GET >=> api logger port
            GET >=> path "/" >=> Files.browseFile homeFolder "index.html"
            GET >=> path "/all" >=> allLogs
            GET >=> Files.browseHome
            RequestErrors.NOT_FOUND "Page not found."
        ]

/// Where a tool that did not start the daemon can find out it is there, and on which port.
let discoveryFile : FileInfo =
    Path.Combine (Path.GetTempPath (), "vite-plugin-fable", $"daemon-%i{Environment.ProcessId}.json")
    |> FileInfo

/// Forget the daemons that are no longer running.
///
/// The clean shutdown below deletes this process's own file, but a daemon that is killed never
/// gets there, so without a sweep the folder fills up with dead ports and a tool reading it has no
/// way to tell which one to talk to.
let removeStaleDiscoveryFiles (folder : DirectoryInfo) : unit =
    if folder.Exists then
        for file in folder.EnumerateFiles "daemon-*.json" do
            let alive =
                let name = Path.GetFileNameWithoutExtension file.Name

                match Int32.TryParse (name.Substring "daemon-".Length) with
                | false, _ -> false
                | true, pid ->
                    try
                        not (System.Diagnostics.Process.GetProcessById pid).HasExited
                    with _ ->
                        false

            if not alive then
                try
                    File.Delete file.FullName
                with _ ->
                    ()

let writeDiscoveryFile (port : uint16) (cancellationToken : CancellationToken) : unit =
    try
        let file = discoveryFile
        Directory.CreateDirectory file.Directory.FullName |> ignore
        removeStaleDiscoveryFiles file.Directory

        let contents =
            serialize
                {|
                    pid = Environment.ProcessId
                    port = int port
                    url = $"http://127.0.0.1:%i{port}"
                    api = $"http://127.0.0.1:%i{port}/api"
                    startedAt = processStartedAt
                |}

        File.WriteAllText (file.FullName, contents)

        cancellationToken.Register (fun () ->
            try
                file.Refresh ()

                if file.Exists then
                    File.Delete file.FullName
            with _ ->
                ()
        )
        |> ignore
    with _ ->
        // A temp folder that cannot be written to is not a reason to fail the debug server.
        ()

let startWebserver (logger : InMemoryLogger) (port : uint16) (cancellationToken : CancellationToken) : Async<unit> =
    let conf =
        { defaultConfig with
            cancellationToken = cancellationToken
            homeFolder = Some homeFolder
            logger = zeroSuaveLogger
            bindings = [ HttpBinding.create HTTP IPAddress.Loopback port ]
        }

    (logger :> ILogger).LogDebug "Starting Suave dev server"
    // Set before the server is awaited: the message loop starts publishing as soon as the daemon
    // serves anything, which can be before Suave finishes binding.
    serverEnabled <- true
    writeDiscoveryFile port cancellationToken
    let _listening, server = startWebServerAsync conf (webApp logger port)
    server
