module Fable.Daemon.MSBuild

open System
open System.IO
open System.Diagnostics
open System.Reflection
open Microsoft.Extensions.Logging

let dotnet_msbuild (logger : ILogger) (fsproj : FileInfo) (args : string) : Async<string> =
    backgroundTask {
        let psi = ProcessStartInfo "dotnet"
        let pwd = Assembly.GetEntryAssembly().Location |> Path.GetDirectoryName
        psi.WorkingDirectory <- pwd
        psi.Arguments <- $"msbuild \"%s{fsproj.FullName}\" %s{args}"
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.EnvironmentVariables.["DOTNET_NOLOGO"] <- "1"

        use ps = new Process ()
        ps.StartInfo <- psi

        if not (ps.Start ()) then
            failwith $"Could not start `dotnet msbuild` for %s{fsproj.FullName}"

        // Both pipes have to be drained at the same time. Reading one to the end first blocks here
        // until the child closes it, and a child that fills the other pipe's buffer meanwhile
        // blocks writing to it: neither side moves again. Nothing times out on the way back, so
        // the plugin would wait on a design time build that never finishes.
        let readOutput = ps.StandardOutput.ReadToEndAsync ()
        let readError = ps.StandardError.ReadToEndAsync ()
        let! output = readOutput
        let! error = readError
        do! ps.WaitForExitAsync ()

        // The exit code decides, and only the exit code. MSBuild and NuGet write warnings to
        // stderr on runs that succeed, and failing on those reported a warning as a project that
        // could not be cracked.
        if ps.ExitCode <> 0 then
            // MSBuild reports its own failures on stdout (`error MSB4057: ...`) and typically
            // leaves stderr empty, so a message built from stderr alone names no reason at all.
            let detail =
                [ output ; error ]
                |> List.filter (String.IsNullOrWhiteSpace >> not)
                |> String.concat "\n"

            logger.LogCritical (
                "dotnet msbuild \"{fsproj}\" {args} exited with {exitCode}:\n{detail}",
                fsproj.FullName,
                args,
                ps.ExitCode,
                detail
            )

            failwithf
                $"In %s{pwd}:\ndotnet msbuild \"%s{fsproj.FullName}\" %s{args} failed with exit code %i{ps.ExitCode}\n%s{detail}"

        if not (String.IsNullOrWhiteSpace error) then
            logger.LogWarning (
                "dotnet msbuild \"{fsproj}\" {args} wrote to stderr:\n{error}",
                fsproj.FullName,
                args,
                error
            )

        return output.Trim ()
    }
    |> Async.AwaitTask
