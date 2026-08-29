module Fable.Daemon.Debug

open System.Threading
open Microsoft.Extensions.Logging

/// A custom logger that captures everything in memory and sends events via WebSockets to the connect debug tool.
type InMemoryLogger =
    new : unit -> InMemoryLogger
    interface ILogger

/// Start a Suave webserver to view all the logs inside the Fable.Daemon process.
val startWebserver : logger : InMemoryLogger -> cancellationToken : CancellationToken -> Async<unit>
