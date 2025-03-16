[<AutoOpenAttribute>]
module Util 

open Fable.Pyxpecto
open Fable.Core


[<Import("it", from = "vitest")>]
let it(name: string, test: unit -> unit) = jsNative

[<Import("describe", from = "vitest")>]
let describe(name: string, testSuit: unit -> unit) = jsNative 