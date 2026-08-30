module Fable.Daemon.MSBuild

open System
open System.IO
open System.Diagnostics
open System.Reflection
open Microsoft.Extensions.Logging

/// Execute `dotnet msbuild` process and capture the stdout.
/// Expected usage is with `--getProperty` and `--getItem` arguments.
val dotnet_msbuild : logger : ILogger -> fsproj : FileInfo -> args : string -> Async<string>
