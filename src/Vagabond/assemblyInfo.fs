﻿namespace System
open System.Reflection

[<assembly: AssemblyVersionAttribute("0.12.11")>]
[<assembly: AssemblyFileVersionAttribute("0.12.11")>]
do ()

module internal AssemblyVersionInformation =
    let [<Literal>] Version = "0.12.11"
    let [<Literal>] InformationalVersion = "0.12.11"
