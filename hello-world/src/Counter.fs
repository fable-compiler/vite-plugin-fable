module Counter

open Browser.Dom
open Browser.Types

let setupCounter (element: Element) =
    
    let mutable counter = 0
    
    let setCounter count =
        counter <- count
        element.innerHTML <- $"count is {counter}" 
        printfn $"counter is: {counter}"
        ()
    
    element.addEventListener("click", fun _ ->  setCounter (counter + 1))
    
    setCounter 0
