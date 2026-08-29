module Fable.Daemon.MSBuild

open System
open System.IO
open System.Diagnostics
open System.Reflection
open Microsoft.Extensions.Logging

/// Same as `dotnet_msbuild` but includes the defines as environment variables.
let dotnet_msbuild_with_defines
    (logger : ILogger)
    (fsproj : FileInfo)
    (args : string)
    (defines : string list)
    : Async<string>
    =
    backgroundTask {
        let psi = ProcessStartInfo "dotnet"
        let pwd = Assembly.GetEntryAssembly().Location |> Path.GetDirectoryName
        psi.WorkingDirectory <- pwd
        psi.Arguments <- $"msbuild \"%s{fsproj.FullName}\" %s{args}"
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.EnvironmentVariables.["DOTNET_NOLOGO"] <- "1"

        if not (List.isEmpty defines) then
            let definesValue = defines |> String.concat ";"
            psi.Environment.Add ("DefineConstants", definesValue)

        use ps = new Process ()
        ps.StartInfo <- psi
        ps.Start () |> ignore
        let output = ps.StandardOutput.ReadToEnd ()
        let error = ps.StandardError.ReadToEnd ()
        do! ps.WaitForExitAsync ()

        if ps.ExitCode <> 0 || not (String.IsNullOrWhiteSpace error) then
            logger.LogCritical ("dotnet msbuild \"{fsproj}\" {args}\n did has {error}", fsproj.FullName, args, error)
            failwithf $"In %s{pwd}:\ndotnet msbuild \"%s{fsproj.FullName}\" %s{args} failed with\n%s{error}"

        return output.Trim ()
    }
    |> Async.AwaitTask

/// Execute `dotnet msbuild` process and capture the stdout.
/// Expected usage is with `--getProperty` and `--getItem` arguments.
let dotnet_msbuild (logger : ILogger) (fsproj : FileInfo) (args : string) : Async<string> =
    dotnet_msbuild_with_defines logger fsproj args List.empty
