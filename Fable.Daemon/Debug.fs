module Fable.Daemon.Debug

open System
open System.Collections.Generic
open System.Text
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

let homeFolder = Path.Combine (__SOURCE_DIRECTORY__, "debug")

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
            li [] [
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

    member val All : LogEntry seq = entries
    member x.Count : int = entries.Count

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
            entries.Enqueue
                {
                    Level = string logLevel
                    Exception = ex
                    Message = formatter.Invoke (state, ex)
                    TimeStamp = DateTime.Now
                }

            broadCastNewMessages ()

        member x.BeginScope<'TState> (_state : 'TState) : IDisposable = null
        member x.IsEnabled (_logLevel : LogLevel) : bool = true

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

let webApp (logger : InMemoryLogger) : WebPart =
    let allLogs ctx =
        let html = logger.All |> HTML.mapLogEntriesToListItems
        (OK html >=> Writers.setMimeType "text/html") ctx

    choose [
        path "/ws" >=> handShake (ws logger)
        GET >=> path "/" >=> Files.browseFile homeFolder "index.html"
        GET >=> path "/all" >=> allLogs
        GET >=> Files.browseHome
        RequestErrors.NOT_FOUND "Page not found."
    ]

let startWebserver (logger : InMemoryLogger) (cancellationToken : CancellationToken) : Async<unit> =
    let conf =
        { defaultConfig with
            cancellationToken = cancellationToken
            homeFolder = Some homeFolder
            logger = zeroSuaveLogger
            bindings = [ HttpBinding.create HTTP IPAddress.Loopback defaultPort ]
        }

    (logger :> ILogger).LogDebug "Starting Suave dev server"
    let _listening, server = startWebServerAsync conf (webApp logger)
    server
