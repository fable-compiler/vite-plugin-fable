module App

open Browser.Dom
open Fable.Core.JsInterop
open Counter

// Import CSS and SVG files
importSideEffects "./style.css"

let viteLogo: string = importDefault "./assets/vite.svg"

printfn $"{viteLogo}"

let javascriptLogo : string = importDefault "./javascript.svg"

printfn $"{javascriptLogo}"

// make html markup available using vscode F# html ext
let html = id

// Create the HTML content
let app = document.querySelector("#app")
app.innerHTML <- 
  html $"""
    <div>
      <a href="https://vite.dev" target="_blank">
        <img src="{viteLogo}" class="logo" alt="Vite logo" />
      </a>
      <a href="https://developer.mozilla.org/en-US/docs/Web/JavaScript" target="_blank">
        <img src="{javascriptLogo}" class="logo vanilla" alt="JavaScript logo" />
      </a>
      <h1>Hello Vite!</h1>
      <div class="card">
        <button id="counter" type="button"></button>
      </div>
      <p class="read-the-docs">
        Click on the Vite logo to learn more
      </p>
    </div>
  """

document.querySelector("#counter")
|> Counter.setupCounter 
