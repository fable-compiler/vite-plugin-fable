module App

open Fable.Core
open Browser.Dom
open Math
open Thoth.Json
open Fable.React

let r = sum 1 19

let someJsonString =
    Encode.object [ "track", Encode.string "Changes" ] |> Encode.toString 4

let h1Element = document.querySelector "#dyn"
h1Element.textContent <- $"Dynamic Fable text %i{r}! %s{someJsonString}"

let root = Feliz.ReactDOM.createRoot(document.getElementById "app")
root.render(Components.RootComponent.El())
