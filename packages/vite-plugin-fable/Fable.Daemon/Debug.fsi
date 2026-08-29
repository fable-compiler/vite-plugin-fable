module Fable.Daemon.Debug

open System
open System.Threading
open Microsoft.Extensions.Logging

/// The port the debug server listens on unless `VITE_PLUGIN_FABLE_DEBUG_PORT` says otherwise.
val defaultPort : uint16

/// A custom logger that captures everything in memory and sends events via WebSockets to the connect debug tool.
type InMemoryLogger =
    new : unit -> InMemoryLogger
    interface ILogger

/// What the last crack established about the project.
///
/// Plain data rather than the cracker's own types: the message loop builds one of these and the
/// endpoints serve it, so nothing about Fable's shapes leaks into the JSON.
type ProjectState =
    {
        Fsproj : FullPath
        Configuration : string
        FableLibrary : FullPath
        Exclude : string list
        NoReflection : bool
        /// Every source file, in compilation order.
        SourceFiles : FullPath array
        /// The MSBuild inputs a change to which forces a re-crack.
        DependentFiles : FullPath array
        TargetFramework : string option
        OutputType : string option
        CompilerArgs : string array
        ProjectReferences : string array
        /// What type-checking the project reported, unfiltered.
        Diagnostics : Diagnostic array
        /// Whether the design time build cache answered the crack.
        CacheReused : bool
        /// Why it could not, as a stable name and the detail behind it. Empty when it was reused.
        CacheReason : string
        CacheDetail : string
        CacheFile : FullPath
        FableModulesCacheFile : FullPath
    }

/// Whether anything is listening. Publishing is a no-op otherwise, so a daemon started without
/// `VITE_PLUGIN_FABLE_DEBUG` pays neither the allocations nor the memory of retaining compiled
/// output.
val isEnabled : unit -> bool

/// Record what a crack established. Drops whatever the previous project compiled, because a crack
/// decides which files the project even has.
val publishProject : project : ProjectState -> unit

/// Record what a full project compile produced.
val publishInitialCompile : compiled : Map<FullPath, JavaScript> -> fromCache : Set<FullPath> -> unit

/// Record what compiling a set of changed files produced, merged over what was compiled before,
/// the way the plugin merges it into its own map.
val publishFileCompile :
    compiled : Map<FullPath, JavaScript> -> diagnostics : Diagnostic array -> requested : FullPath array -> unit

/// Record a served JSON-RPC request for `/api/requests`.
val recordRequest : method : string -> detail : string -> durationMs : float -> outcome : string -> unit

/// Start a Suave webserver to view all the logs inside the Fable.Daemon process, and to answer the
/// JSON endpoints under `/api`.
val startWebserver : logger : InMemoryLogger -> port : uint16 -> cancellationToken : CancellationToken -> Async<unit>
