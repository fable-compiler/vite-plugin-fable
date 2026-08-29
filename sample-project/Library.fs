module App

open Fable.Core
open Browser.Dom
open Math
open Thoth.Json

let r = sum 1 19

let someJsonString =
    Encode.object [ "track", Encode.string "Changes" ] |> Encode.toString 4

let h1Element = document.querySelector "h1"
h1Element.textContent <- $"Dynamic Fable text %i{r}! %s{someJsonString}"

open React

let app = document.querySelector "#app"
ReactDom.createRoot(app).render(JSX.create Components.Component.Component [])
